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
/// Instances follow their IndirectInstance component's transform. They live in
/// the GpuInstanceBuffer; what moves need on the CPU is kept per slot by
/// the InstanceTransformTracker.
/// </summary>
public sealed partial class IndirectRenderer
{
	private readonly GenerationalSlotAllocator instanceSlots = new(GpuInstanceBuffer.MinimumCapacity);
	private readonly InstanceTransformTracker transformTracker = new();
	private readonly GpuInstanceBuffer gpuInstances;

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
			Position = objectToWorld.GetPosition(),
		});
		gpuInstances.Write(slot, data);
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
		gpuInstances.WriteOverrideIndex(slot, overrideIndex);
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
		gpuInstances.Write(slot, default);
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
			gpuInstances.Write(movedInstance.Slot, data);
		}

		Profiler.EndSample();
	}

	private void ResetInstances()
	{
		instanceSlots.Clear();
		transformTracker.Clear();
		gpuInstances.Reset();
	}
}
