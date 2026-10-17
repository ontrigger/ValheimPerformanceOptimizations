using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using IndirectRendering.Burst;
using IndirectRendering.Extensions;

namespace IndirectRendering;

/// <summary>
/// Registered instances bucketed into square XZ cells, so the CPU can skip
/// submitting batches with no instance near a view; see MarkVisibleBatchesJob.
/// </summary>
internal sealed class BatchCellGrid : IDisposable
{
	private sealed class Cell
	{
		internal int2 Key;
		internal int Index;
		internal int InstanceCount;
		internal float3 BoundsMinimum;
		internal float3 BoundsMaximum;
		internal BatchCullEntry[] Batches = new BatchCullEntry[8];
		internal int BatchCount;
		internal readonly Dictionary<int, int> EntryIndexByBatch = new();
	}

	private readonly Dictionary<int2, Cell> cellByKey = new();
	private readonly List<Cell> cells = new();
	private readonly float inverseCellSize;
	private bool dirty;

	private NativeArray<BatchCullCell> packedCells;
	private NativeArray<BatchCullEntry> packedEntries;

	internal BatchCellGrid(float cellSize)
	{
		inverseCellSize = 1f / cellSize;
	}

	internal int CellCount => cells.Count;

	/// <summary>
	/// position picks the cell and must be passed again to Remove; lodRange is
	/// the instance's packed LOD range, see IndirectRenderer.Register.
	/// </summary>
	internal void Add(int batchIndex, float3 position, float3 boundsMinimum, float3 boundsMaximum, float4 lodRange)
	{
		var key = GetCellKey(position);
		if (!cellByKey.TryGetValue(key, out var cell))
		{
			cell = new Cell { Key = key, Index = cells.Count };
			cellByKey.Add(key, cell);
			cells.Add(cell);
		}

		if (cell.InstanceCount == 0)
		{
			cell.BoundsMinimum = boundsMinimum;
			cell.BoundsMaximum = boundsMaximum;
		}

		cell.BoundsMinimum = math.min(cell.BoundsMinimum, math.min(boundsMinimum, lodRange.xyz));
		cell.BoundsMaximum = math.max(cell.BoundsMaximum, math.max(boundsMaximum, lodRange.xyz));
		cell.InstanceCount += 1;
		dirty = true;

		var distances = math.asuint(lodRange.w);
		var startDistance = math.f16tof32(distances);
		var endDistance = math.f16tof32(distances >> 16);
		if (!cell.EntryIndexByBatch.TryGetValue(batchIndex, out var entryIndex))
		{
			if (cell.BatchCount == cell.Batches.Length)
			{
				Array.Resize(ref cell.Batches, cell.BatchCount * 2);
			}

			cell.EntryIndexByBatch.Add(batchIndex, cell.BatchCount);
			cell.Batches[cell.BatchCount] = new BatchCullEntry
			{
				BatchIndex = batchIndex, InstanceCount = 1,
				MinimumStartDistance = startDistance, MaximumEndDistance = endDistance,
				BoundsMinimum = boundsMinimum, BoundsMaximum = boundsMaximum,
			};
			cell.BatchCount += 1;
			return;
		}

		// Like the cell bounds, an entry's bounds only shrink once its last
		// instance is removed.
		ref var entry = ref cell.Batches[entryIndex];
		entry.InstanceCount += 1;
		entry.MinimumStartDistance = math.min(entry.MinimumStartDistance, startDistance);
		entry.MaximumEndDistance = math.max(entry.MaximumEndDistance, endDistance);
		entry.BoundsMinimum = math.min(entry.BoundsMinimum, boundsMinimum);
		entry.BoundsMaximum = math.max(entry.BoundsMaximum, boundsMaximum);
	}

	internal void Remove(int batchIndex, float3 position)
	{
		if (!cellByKey.TryGetValue(GetCellKey(position), out var cell))
		{
			return;
		}

		if (!cell.EntryIndexByBatch.TryGetValue(batchIndex, out var entryIndex))
		{
			return;
		}

		dirty = true;
		cell.Batches[entryIndex].InstanceCount -= 1;
		if (cell.Batches[entryIndex].InstanceCount == 0)
		{
			cell.EntryIndexByBatch.Remove(batchIndex);
			cell.BatchCount -= 1;
			var lastIndex = cell.BatchCount;
			if (entryIndex != lastIndex)
			{
				var moved = cell.Batches[lastIndex];
				cell.Batches[entryIndex] = moved;
				cell.EntryIndexByBatch[moved.BatchIndex] = entryIndex;
			}
		}

		cell.InstanceCount -= 1;
		if (cell.InstanceCount != 0)
		{
			return;
		}

		cellByKey.Remove(cell.Key);
		var last = cells[cells.Count - 1];
		cells[cell.Index] = last;
		last.Index = cell.Index;
		cells.RemoveAt(cells.Count - 1);
	}

	/// <summary>
	/// Moves an instance added at oldPosition to newPosition, with its new
	/// bounds and LOD range. Within one cell the entry and cell bounds only
	/// grow, as with Add, so they cover the instance's path through the cell.
	/// </summary>
	internal void Move(
		int batchIndex,
		float3 oldPosition,
		float3 newPosition,
		float3 boundsMinimum,
		float3 boundsMaximum,
		float4 lodRange)
	{
		var key = GetCellKey(newPosition);
		if (!key.Equals(GetCellKey(oldPosition)))
		{
			Remove(batchIndex, oldPosition);
			Add(batchIndex, newPosition, boundsMinimum, boundsMaximum, lodRange);
			return;
		}

		if (!cellByKey.TryGetValue(key, out var cell) || !cell.EntryIndexByBatch.TryGetValue(batchIndex, out var entryIndex))
		{
			return;
		}

		// Moving instances soon cover their path, so most moves grow nothing
		// and leave the packed cells as they are. The cell bounds contain
		// every entry's bounds.
		ref var entry = ref cell.Batches[entryIndex];
		var lodReference = lodRange.xyz;
		if (math.all((boundsMinimum >= entry.BoundsMinimum) & (boundsMaximum <= entry.BoundsMaximum)
			& (lodReference >= cell.BoundsMinimum) & (lodReference <= cell.BoundsMaximum)))
		{
			return;
		}

		entry.BoundsMinimum = math.min(entry.BoundsMinimum, boundsMinimum);
		entry.BoundsMaximum = math.max(entry.BoundsMaximum, boundsMaximum);
		cell.BoundsMinimum = math.min(cell.BoundsMinimum, math.min(boundsMinimum, lodReference));
		cell.BoundsMaximum = math.max(cell.BoundsMaximum, math.max(boundsMaximum, lodReference));
		dirty = true;
	}

	internal void Clear()
	{
		cellByKey.Clear();
		cells.Clear();
		dirty = true;
	}

	/// <summary>
	/// Returns the cells and their batch entries packed for
	/// MarkVisibleBatchesJob, repacking them if they changed. Must not run
	/// while a job reads the previous arrays.
	/// </summary>
	internal void Pack(out NativeArray<BatchCullCell> cellArray, out NativeArray<BatchCullEntry> entryArray)
	{
		if (dirty)
		{
			var entryCount = 0;
			foreach (var cell in cells)
			{
				entryCount += cell.BatchCount;
			}

			packedCells.EnsureCapacity(cells.Count);
			packedEntries.EnsureCapacity(entryCount);
			var entryStart = 0;
			for (var index = 0; index < cells.Count; index += 1)
			{
				var cell = cells[index];
				NativeArray<BatchCullEntry>.Copy(cell.Batches, 0, packedEntries, entryStart, cell.BatchCount);
				packedCells[index] = new BatchCullCell
				{
					Center = (cell.BoundsMinimum + cell.BoundsMaximum) * 0.5f,
					Extents = (cell.BoundsMaximum - cell.BoundsMinimum) * 0.5f,
					EntryStart = entryStart, EntryCount = cell.BatchCount,
				};
				entryStart += cell.BatchCount;
			}

			dirty = false;
		}

		cellArray = packedCells;
		entryArray = packedEntries;
	}

	public void Dispose()
	{
		packedCells.DisposeIfCreated();
		packedEntries.DisposeIfCreated();

		dirty = true;
	}

	private int2 GetCellKey(float3 position)
	{
		return (int2)math.floor(position.xz * inverseCellSize);
	}
}
