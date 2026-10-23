using System;
using IndirectRendering.Burst;
using IndirectRendering.Extensions;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;

namespace IndirectRendering;

/// <summary>
/// Keeps what the instance slots need on the CPU and follows their
/// IndirectInstance components' transforms through Unity's ObjectDispatcher,
/// which reports only that exact type, enabled or not. Collect returns each
/// slot whose component moved since the last call.
/// </summary>
internal sealed class InstanceTransformTracker : IDisposable
{
	private const int MinimumCapacity = 256;

	private ObjectDispatcher dispatcher = new();

	private NativeParallelMultiHashMap<int, int> slotsByInstanceId;
	private int trackedCount;

	private NativeArray<InstanceSlot> slots;

	private NativeArray<float3> batchBoundsCenters;
	private NativeArray<float3> batchBoundsExtents;

	private NativeArray<MovedInstance> moved;
	private NativeArray<int> movedCount;

	internal InstanceTransformTracker()
	{
		dispatcher.EnableTransformTracking<IndirectInstance>(ObjectDispatcher.TransformTrackingType.GlobalTRS);
		slotsByInstanceId = new NativeParallelMultiHashMap<int, int>(MinimumCapacity, Allocator.Persistent);
		moved = new NativeArray<MovedInstance>(MinimumCapacity, Allocator.Persistent);
		movedCount = new NativeArray<int>(1, Allocator.Persistent);
	}

	internal void SetBatchBounds(int batchIndex, Bounds meshBounds)
	{
		batchBoundsCenters.GrowCapacity(batchIndex + 1, MinimumCapacity);
		batchBoundsExtents.GrowCapacity(batchIndex + 1, MinimumCapacity);
		batchBoundsCenters[batchIndex] = meshBounds.center;
		batchBoundsExtents[batchIndex] = meshBounds.extents;
	}

	internal void Track(int slot, in InstanceSlot data)
	{
		slots.GrowCapacity(slot + 1, MinimumCapacity);
		if (slots[slot].ComponentId != 0)
		{
			Untrack(slot);
		}

		slots[slot] = data;
		slotsByInstanceId.Add(data.ComponentId, slot);
		trackedCount += 1;
	}

	/// <summary>
	/// Returns the slot's data as it was tracked.
	/// </summary>
	internal InstanceSlot Untrack(int slot)
	{
		var data = slots[slot];
		slotsByInstanceId.Remove(data.ComponentId, slot);
		trackedCount -= 1;

		slots[slot] = default;
		return data;
	}

	internal InstanceSlot Get(int slot)
	{
		return slots[slot];
	}

	internal void SetOverrideIndex(int slot, int overrideIndex)
	{
		var data = slots[slot];
		data.OverrideIndex = overrideIndex;
		slots[slot] = data;
	}

	/// <summary>
	/// Drains the components that moved since the last call and returns the
	/// first count elements: one per slot of a tracked component. The array is
	/// valid until the next call.
	/// </summary>
	internal NativeArray<MovedInstance> Collect(out int count)
	{
		Profiler.BeginSample("GetTransformChanges");
		var changes = dispatcher.GetTransformChangesAndClear<IndirectInstance>(
			ObjectDispatcher.TransformTrackingType.GlobalTRS,
			Allocator.TempJob);
		Profiler.EndSample();

		try
		{
			count = 0;
			if (trackedCount == 0 || changes.transformedID.Length == 0)
			{
				return moved;
			}

			Profiler.BeginSample("CollectMovedInstances");
			count = RunCollect(changes);
			if (count > moved.Length)
			{
				moved.EnsureCapacity(count, MinimumCapacity);
				count = RunCollect(changes);
			}
			Profiler.EndSample();

			return moved;
		}
		finally
		{
			changes.Dispose();
		}
	}

	private int RunCollect(in TransformDispatchData changes)
	{
		new CollectMovedInstancesJob
		{
			ComponentIds = changes.transformedID,
			LocalToWorld = changes.localToWorldMatrices.Reinterpret<float4x4>(),
			ComponentSlots = slotsByInstanceId,
			Slots = slots,
			BatchBoundsCenters = batchBoundsCenters,
			BatchBoundsExtents = batchBoundsExtents,
			Moved = moved,
			MovedCount = movedCount,
		}.Run();
		return movedCount[0];
	}

	internal void Clear()
	{
		slotsByInstanceId.Clear();
		trackedCount = 0;
		slots.DisposeIfCreated();
		dispatcher.GetTransformChangesAndClear<IndirectInstance>(
			ObjectDispatcher.TransformTrackingType.GlobalTRS,
			Allocator.TempJob).Dispose();
	}

	public void Dispose()
	{
		dispatcher?.Dispose();
		dispatcher = null;
		if (slotsByInstanceId.IsCreated)
		{
			slotsByInstanceId.Dispose();
		}

		slots.DisposeIfCreated();
		batchBoundsCenters.DisposeIfCreated();
		batchBoundsExtents.DisposeIfCreated();
		moved.DisposeIfCreated();
		movedCount.DisposeIfCreated();
	}
}
