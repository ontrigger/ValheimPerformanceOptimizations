using System;
using System.Runtime.InteropServices;
using IndirectRendering.Extensions;
using UnityEngine;
using UnityEngine.Profiling;

namespace IndirectRendering;

internal sealed class GpuInstanceBuffer
{
	internal const int MinimumCapacity = 256;
	// Marks a scatter that only writes the override index of its slot.
	private const uint OverrideOnlyScatterFlag = 0x80000000u;

	private static readonly int ScatterSlotsId = Shader.PropertyToID("_ScatterSlots");
	private static readonly int ScatterInstancesId = Shader.PropertyToID("_ScatterInstances");
	private static readonly int ScatterOutputInstancesId = Shader.PropertyToID("_ScatterOutputInstances");
	private static readonly int ScatterCountId = Shader.PropertyToID("_ScatterCount");
	private static readonly int CopySourceInstancesId = Shader.PropertyToID("_CopySourceInstances");

	private readonly IndirectRendererShaders shaders;

	// a slot's pending write is at scatterIndices[slot] - 1, or none for 0.
	private uint[] scatterSlots = new uint[64];
	private IndirectInstanceData[] scatterInstances = new IndirectInstanceData[64];
	private int[] scatterIndices = new int[MinimumCapacity];
	private int scatterCount;
	private GraphicsBuffer scatterSlotsBuffer;
	private GraphicsBuffer scatterInstancesBuffer;
	private GraphicsBuffer buffer;

	internal GpuInstanceBuffer(IndirectRendererShaders shaders)
	{
		this.shaders = shaders;
	}

	internal GraphicsBuffer Buffer => buffer;

	internal void Write(int slot, in IndirectInstanceData data)
	{
		QueueScatter(slot, data, false);
	}

	/// <summary>
	/// Writes only the override index, which lives in the bottom row of
	/// worldToObject; see IndirectRenderer.PackInstance.
	/// </summary>
	internal void WriteOverrideIndex(int slot, int overrideIndex)
	{
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
	/// Grows the buffer to slotCount slots and sends the queued writes.
	/// Returns whether the buffer was replaced.
	/// </summary>
	internal bool Flush(int slotCount)
	{
		var replaced = EnsureBuffer(slotCount);
		if (scatterCount == 0) { return replaced; }

		Profiler.BeginSample("ScatterInstances");
		if (scatterSlotsBuffer == null || scatterSlotsBuffer.count < scatterCount)
		{
			GraphicsBufferX.Release(ref scatterSlotsBuffer);
			GraphicsBufferX.Release(ref scatterInstancesBuffer);
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
		computeShader.SetBuffer(kernel, ScatterOutputInstancesId, buffer);
		computeShader.SetInt(ScatterCountId, scatterCount);
		Dispatch(kernel, scatterCount, shaders.ScatterInstancesGroupSize);

		for (var index = 0; index < scatterCount; index += 1)
		{
			scatterIndices[scatterSlots[index] & ~OverrideOnlyScatterFlag] = 0;
		}

		scatterCount = 0;
		Profiler.EndSample();
		return replaced;
	}

	private bool EnsureBuffer(int slotCount)
	{
		var count = Math.Max(slotCount, MinimumCapacity);
		if (buffer != null && buffer.count >= count)
		{
			return false;
		}

		var grown = new GraphicsBuffer(
			GraphicsBuffer.Target.Structured,
			Mathf.NextPowerOfTwo(count),
			Marshal.SizeOf<IndirectInstanceData>());
		if (buffer != null)
		{
			var computeShader = shaders.FrustumCullShader;
			var kernel = shaders.CopyInstancesKernel;
			computeShader.SetBuffer(kernel, CopySourceInstancesId, buffer);
			computeShader.SetBuffer(kernel, ScatterOutputInstancesId, grown);
			computeShader.SetInt(ScatterCountId, buffer.count);
			Dispatch(kernel, buffer.count, shaders.CopyInstancesGroupSize);
			buffer.Release();
		}

		buffer = grown;
		return true;
	}

	private void Dispatch(int kernel, int threadCount, int groupSize)
	{
		ComputeDispatch.GetDimensions(
			ComputeDispatch.GetThreadGroupCount(threadCount, groupSize),
			out var dispatchX, out var dispatchY);
		shaders.FrustumCullShader.Dispatch(kernel, dispatchX, dispatchY, 1);
	}

	internal void Reset()
	{
		Array.Clear(scatterIndices, 0, scatterIndices.Length);
		scatterCount = 0;
		GraphicsBufferX.Release(ref buffer);
		GraphicsBufferX.Release(ref scatterSlotsBuffer);
		GraphicsBufferX.Release(ref scatterInstancesBuffer);
	}
}
