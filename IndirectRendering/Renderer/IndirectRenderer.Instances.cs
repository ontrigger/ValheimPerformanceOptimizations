using System;
using System.Runtime.InteropServices;
using IndirectRendering.Burst;
using IndirectRendering.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;

namespace IndirectRendering;

[StructLayout(LayoutKind.Sequential)]
internal struct IndirectInstanceData
{
	internal Matrix4x4 ObjectToWorld;
	internal Matrix4x4 WorldToObject;
}
/// <summary>
/// Instances follow their IndirectInstance component's transform. They live in a GPU-only
/// buffer, written only by the ScatterInstances kernel; what moves need on the
/// CPU is kept per slot by the InstanceTransformTracker.
/// </summary>
public sealed partial class IndirectRenderer
{
	private const int MinimumInstanceCapacity = 256;
	// Marks a scatter that only writes the override index of its slot.
	private const uint OverrideOnlyScatterFlag = 0x80000000u;

	private static readonly int ScatterSlotsId = Shader.PropertyToID("_ScatterSlots");
	private static readonly int ScatterInstancesId = Shader.PropertyToID("_ScatterInstances");
	private static readonly int ScatterOutputInstancesId = Shader.PropertyToID("_ScatterOutputInstances");
	private static readonly int ScatterCountId = Shader.PropertyToID("_ScatterCount");
	private static readonly int CopySourceInstancesId = Shader.PropertyToID("_CopySourceInstances");

	private readonly GenerationalSlotAllocator instanceSlots = new(MinimumInstanceCapacity);
	private readonly InstanceTransformTracker transformTracker = new();
	private GraphicsBuffer instanceBuffer;

	// The writes ScatterInstances sends, at most one per slot: a slot's
	// pending write is at scatterIndices[slot] - 1, or none for 0.
	private uint[] scatterSlots = new uint[64];
	private IndirectInstanceData[] scatterInstances = new IndirectInstanceData[64];
	private int[] scatterIndices = new int[MinimumInstanceCapacity];
	private int scatterCount;
	private GraphicsBuffer scatterSlotsBuffer;
	private GraphicsBuffer scatterInstancesBuffer;

	/// <summary>
	/// Adds an instance of batch batchId that follows the IndirectInstance with
	/// instance id componentId, which may be disabled; objectToWorld is its
	/// current localToWorldMatrix. lodRange packs the LOD test, see
	/// PieceFrustumCull.compute, and its reference point moves with the
	/// component; overrideIndex is from AllocateOverride, or 0 for none.
	/// </summary>
	public GenerationalHandle Register(
		int batchId,
		int componentId,
		Matrix4x4 objectToWorld,
		Vector4 lodRange,
		int overrideIndex)
	{
		if (!AcceptsRegistrations)
		{
			return GenerationalHandle.Invalid;
		}

		if (componentId == 0)
		{
			throw new ArgumentException("An instance needs a component id.", nameof(componentId));
		}

		var runtimeBatch = GetRuntimeBatch(batchId);
		var data = PackInstance(runtimeBatch, objectToWorld, lodRange, overrideIndex);
		var handle = instanceSlots.Allocate();
		var slot = instanceSlots.GetSlotIndex(handle);
		// MultiplyPoint3x4 ignores the bottom row and its packed values.
		transformTracker.Track(slot, new InstanceSlot
		{
			ComponentId = componentId,
			BatchIndex = runtimeBatch.Index,
			OverrideIndex = overrideIndex,
			LodDistances = lodRange.w,
			LocalLodReference = data.WorldToObject.MultiplyPoint3x4(lodRange),
			Position = GetInstancePosition(objectToWorld),
		});
		QueueScatter(slot, data, false);
		AddToBatch(runtimeBatch, objectToWorld, lodRange);
		return handle;
	}

	/// <summary>
	/// Points an instance at an override slot. The index lives in the bottom
	/// row of worldToObject, which is (0, 0, 0, 1) for every affine transform;
	/// the shader reads it and restores the row.
	/// </summary>
	public void SetInstanceOverride(GenerationalHandle instanceHandle, int overrideIndex)
	{
		if (!AcceptsRegistrations || !instanceSlots.Contains(instanceHandle))
		{
			return;
		}

		var slot = instanceSlots.GetSlotIndex(instanceHandle);
		if (transformTracker.Get(slot).OverrideIndex == overrideIndex)
		{
			return;
		}

		transformTracker.SetOverrideIndex(slot, overrideIndex);
		var pending = scatterIndices[slot] - 1;
		if (pending >= 0)
		{
			scatterInstances[pending].WorldToObject.m30 = overrideIndex;
			return;
		}

		var data = default(IndirectInstanceData);
		data.WorldToObject.m30 = overrideIndex;
		QueueScatter(slot, data, true);
	}

	public void Unregister(ref GenerationalHandle instanceHandle)
	{
		var handle = instanceHandle;
		instanceHandle = GenerationalHandle.Invalid;
		if (!AcceptsRegistrations || !instanceSlots.Release(handle, out var slot))
		{
			return;
		}

		var data = transformTracker.Untrack(slot);
		// Zeroed slots match no batch.
		QueueScatter(slot, default, false);
		RemoveFromBatch(runtimeBatches[data.BatchIndex], data.Position);
	}

	private void ApplyMovedInstances()
	{
		Profiler.BeginSample("ApplyMovedInstances");
		var moved = transformTracker.Collect(out var count);
		for (var i = 0; i < count; i += 1)
		{
			var movedInstance = moved[i];
			var objectToWorld = movedInstance.ObjectToWorld;
			var runtimeBatch = runtimeBatches[movedInstance.BatchIndex];
			var boundsMinimum = movedInstance.BoundsCenter - movedInstance.BoundsExtents;
			var boundsMaximum = movedInstance.BoundsCenter + movedInstance.BoundsExtents;
			batchCells.Move(
				runtimeBatch.Index,
				movedInstance.PreviousPosition,
				objectToWorld.c3.xyz,
				boundsMinimum,
				boundsMaximum,
				new float4(objectToWorld.c0.w, objectToWorld.c1.w, objectToWorld.c2.w, objectToWorld.c3.w));
			GrowWorldBounds(runtimeBatch, boundsMinimum, boundsMaximum);
			var data = new IndirectInstanceData
			{
				ObjectToWorld = objectToWorld, WorldToObject = movedInstance.WorldToObject,
			};
			QueueScatter(movedInstance.Slot, data, false);
		}

		Profiler.EndSample();
	}

	// A later write to a slot replaces its pending one, since the kernel
	// writes in no particular order. A full write clears the override-only flag.
	private void QueueScatter(int slot, in IndirectInstanceData data, bool overrideOnly)
	{
		if (slot >= scatterIndices.Length)
		{
			Array.Resize(ref scatterIndices, Mathf.NextPowerOfTwo(slot + 1));
		}

		var index = scatterIndices[slot] - 1;
		if (index < 0)
		{
			if (scatterCount == scatterSlots.Length)
			{
				Array.Resize(ref scatterSlots, scatterCount * 2);
				Array.Resize(ref scatterInstances, scatterCount * 2);
			}

			index = scatterCount;
			scatterCount += 1;
			scatterIndices[slot] = index + 1;
		}

		scatterSlots[index] = (uint)slot | (overrideOnly ? OverrideOnlyScatterFlag : 0u);
		scatterInstances[index] = data;
	}

	/// <summary>
	/// Grows the instance buffer to the slots in use and sends the queued
	/// writes. Returns whether the buffer was replaced.
	/// </summary>
	private bool ScatterInstances()
	{
		var replaced = EnsureInstanceBuffer();
		if (scatterCount == 0) { return replaced; }

		Profiler.BeginSample("ScatterInstances");
		if (scatterSlotsBuffer == null || scatterSlotsBuffer.count < scatterCount)
		{
			ReleaseBuffer(ref scatterSlotsBuffer);
			ReleaseBuffer(ref scatterInstancesBuffer);
			var capacity = Mathf.NextPowerOfTwo(scatterCount);
			scatterSlotsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, sizeof(uint));
			scatterInstancesBuffer = new GraphicsBuffer(
				GraphicsBuffer.Target.Structured,
				capacity,
				Marshal.SizeOf<IndirectInstanceData>());
		}

		scatterSlotsBuffer.SetData(scatterSlots, 0, 0, scatterCount);
		scatterInstancesBuffer.SetData(scatterInstances, 0, 0, scatterCount);

		var computeShader = shaders.FrustumCullShader;
		var kernel = shaders.ScatterInstancesKernel;
		computeShader.SetBuffer(kernel, ScatterSlotsId, scatterSlotsBuffer);
		computeShader.SetBuffer(kernel, ScatterInstancesId, scatterInstancesBuffer);
		computeShader.SetBuffer(kernel, ScatterOutputInstancesId, instanceBuffer);
		computeShader.SetInt(ScatterCountId, scatterCount);
		DispatchInstanceKernel(kernel, scatterCount, shaders.ScatterInstancesGroupSize);

		for (var index = 0; index < scatterCount; index += 1)
		{
			scatterIndices[scatterSlots[index] & ~OverrideOnlyScatterFlag] = 0;
		}

		scatterCount = 0;
		Profiler.EndSample();
		return replaced;
	}

	// Every slot below the high water mark has been written by a scatter, so
	// a grown buffer needs only the old one's contents.
	private bool EnsureInstanceBuffer()
	{
		var count = Math.Max(instanceSlots.HighWaterMark, MinimumInstanceCapacity);
		if (instanceBuffer != null && instanceBuffer.count >= count)
		{
			return false;
		}

		var grown = new GraphicsBuffer(
			GraphicsBuffer.Target.Structured,
			Mathf.NextPowerOfTwo(count),
			Marshal.SizeOf<IndirectInstanceData>());
		if (instanceBuffer != null)
		{
			var computeShader = shaders.FrustumCullShader;
			var kernel = shaders.CopyInstancesKernel;
			computeShader.SetBuffer(kernel, CopySourceInstancesId, instanceBuffer);
			computeShader.SetBuffer(kernel, ScatterOutputInstancesId, grown);
			computeShader.SetInt(ScatterCountId, instanceBuffer.count);
			DispatchInstanceKernel(kernel, instanceBuffer.count, shaders.CopyInstancesGroupSize);
			instanceBuffer.Release();
		}

		instanceBuffer = grown;
		return true;
	}

	private void DispatchInstanceKernel(int kernel, int threadCount, int groupSize)
	{
		ComputeDispatch.GetDimensions(
			ComputeDispatch.GetThreadGroupCount(threadCount, groupSize),
			out var dispatchX, out var dispatchY);
		shaders.FrustumCullShader.Dispatch(kernel, dispatchX, dispatchY, 1);
	}

	private void ResetInstances()
	{
		instanceSlots.Clear();
		transformTracker.Clear();
		Array.Clear(scatterIndices, 0, scatterIndices.Length);
		scatterCount = 0;
		ReleaseBuffer(ref instanceBuffer);
		ReleaseBuffer(ref scatterSlotsBuffer);
		ReleaseBuffer(ref scatterInstancesBuffer);
	}
}
