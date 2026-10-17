using System;
using System.Collections.Generic;

namespace IndirectRendering.Collections;

/// <summary>
/// Stores values densely while exposing stable index-plus-generation handles.
/// Removing an item uses swap-back and therefore never shifts the remaining
/// dense values.
/// </summary>
internal sealed class GenerationalSparseSet<T>
{
	private struct SparseSlot
	{
		internal uint Generation;
		internal int DenseIndex;
		internal int NextFree;
		internal byte Occupied;
	}

	private T[] denseValues;
	private int[] denseToSparse;
	private readonly List<SparseSlot> sparseSlots;
	private int denseCount;
	private int freeHead = -1;

	internal GenerationalSparseSet(int capacity = 0)
	{
		denseValues = capacity > 0 ? new T[capacity] : Array.Empty<T>();
		denseToSparse = capacity > 0 ? new int[capacity] : Array.Empty<int>();
		sparseSlots = capacity > 0 ? new List<SparseSlot>(capacity) : new List<SparseSlot>();
	}

	internal int Count => denseCount;

	internal ArraySegment<T> DenseValues => new(denseValues, 0, denseCount);

	internal GenerationalHandle Add(in T value)
	{
		int sparseIndex;
		SparseSlot slot;
		if (freeHead >= 0)
		{
			sparseIndex = freeHead;
			slot = sparseSlots[sparseIndex];
			freeHead = slot.NextFree;
		}
		else
		{
			sparseIndex = sparseSlots.Count;
			slot = new SparseSlot { Generation = 1, DenseIndex = -1 };
			sparseSlots.Add(slot);
		}

		var denseIndex = denseCount;
		EnsureDenseCapacity(denseIndex + 1);
		denseValues[denseIndex] = value;
		denseToSparse[denseIndex] = sparseIndex;
		denseCount += 1;
		slot.DenseIndex = denseIndex;
		slot.NextFree = -1;
		slot.Occupied = 1;
		sparseSlots[sparseIndex] = slot;
		return new GenerationalHandle((uint)sparseIndex, slot.Generation);
	}

	internal bool Contains(GenerationalHandle handle)
	{
		return TryGetSparseSlot(handle, out _);
	}

	internal bool TryGet(GenerationalHandle handle, out T value)
	{
		if (TryGetSparseSlot(handle, out var slot))
		{
			value = denseValues[slot.DenseIndex];
			return true;
		}

		value = default;
		return false;
	}

	internal ref T GetRef(GenerationalHandle handle)
	{
		if (!TryGetSparseSlot(handle, out var slot))
		{
			throw new InvalidOperationException($"Unknown or stale sparse-set handle: {handle}.");
		}

		return ref denseValues[slot.DenseIndex];
	}

	internal bool Remove(GenerationalHandle handle, out T removedValue)
	{
		if (!TryGetSparseSlot(handle, out var removedSlot))
		{
			removedValue = default;
			return false;
		}

		var removedDenseIndex = removedSlot.DenseIndex;
		var lastDenseIndex = denseCount - 1;
		removedValue = denseValues[removedDenseIndex];

		if (removedDenseIndex != lastDenseIndex)
		{
			var movedSparseIndex = denseToSparse[lastDenseIndex];
			denseValues[removedDenseIndex] = denseValues[lastDenseIndex];
			denseToSparse[removedDenseIndex] = movedSparseIndex;

			var movedSlot = sparseSlots[movedSparseIndex];
			movedSlot.DenseIndex = removedDenseIndex;
			sparseSlots[movedSparseIndex] = movedSlot;
		}

		denseValues[lastDenseIndex] = default;
		denseToSparse[lastDenseIndex] = default;
		denseCount -= 1;
		removedSlot.Occupied = 0;
		removedSlot.DenseIndex = -1;
		removedSlot.Generation = NextGeneration(removedSlot.Generation);
		removedSlot.NextFree = freeHead;
		freeHead = checked((int)handle.Index);
		sparseSlots[freeHead] = removedSlot;
		return true;
	}

	internal GenerationalHandle GetHandleAtDenseIndex(int denseIndex)
	{
		if ((uint)denseIndex >= (uint)denseCount)
		{
			throw new ArgumentOutOfRangeException(nameof(denseIndex));
		}

		var sparseIndex = denseToSparse[denseIndex];
		var slot = sparseSlots[sparseIndex];
		return new GenerationalHandle((uint)sparseIndex, slot.Generation);
	}

	internal void Clear()
	{
		for (var index = 0; index < sparseSlots.Count; index += 1)
		{
			var slot = sparseSlots[index];
			// Bump free slots too, so no stale handle becomes valid again after reuse.
			slot.Generation = NextGeneration(slot.Generation);

			slot.Occupied = 0;
			slot.DenseIndex = -1;
			slot.NextFree = index + 1 < sparseSlots.Count ? index + 1 : -1;
			sparseSlots[index] = slot;
		}

		Array.Clear(denseValues, 0, denseCount);
		denseCount = 0;
		freeHead = sparseSlots.Count == 0 ? -1 : 0;
	}

	/// <summary>
	/// Checks all bidirectional mappings and the embedded free list.
	/// </summary>
	internal void Validate()
	{
		var freeEntries = new bool[sparseSlots.Count];
		var freeCount = 0;
		for (var sparseIndex = freeHead; sparseIndex >= 0;)
		{
			if ((uint)sparseIndex >= (uint)sparseSlots.Count || freeEntries[sparseIndex])
			{
				throw new InvalidOperationException("Sparse-set free list is invalid or cyclic.");
			}

			freeEntries[sparseIndex] = true;
			freeCount += 1;
			sparseIndex = sparseSlots[sparseIndex].NextFree;
		}

		for (var denseIndex = 0; denseIndex < denseCount; denseIndex += 1)
		{
			var sparseIndex = denseToSparse[denseIndex];
			if ((uint)sparseIndex >= (uint)sparseSlots.Count)
			{
				throw new InvalidOperationException("Sparse-set dense-to-sparse mapping is out of range.");
			}

			var slot = sparseSlots[sparseIndex];
			if (slot.Occupied == 0 || slot.DenseIndex != denseIndex || freeEntries[sparseIndex])
			{
				throw new InvalidOperationException("Sparse-set bidirectional mapping is inconsistent.");
			}
		}

		var occupiedCount = 0;
		for (var sparseIndex = 0; sparseIndex < sparseSlots.Count; sparseIndex += 1)
		{
			var slot = sparseSlots[sparseIndex];
			if (slot.Occupied != 0)
			{
				occupiedCount += 1;
				if (freeEntries[sparseIndex] || (uint)slot.DenseIndex >= (uint)denseCount ||
				    denseToSparse[slot.DenseIndex] != sparseIndex)
				{
					throw new InvalidOperationException("Sparse-set occupied slot is inconsistent.");
				}
			}
		}

		if (occupiedCount != denseCount || occupiedCount + freeCount != sparseSlots.Count)
		{
			throw new InvalidOperationException("Sparse-set occupied/free counts are inconsistent.");
		}
	}

	internal void Dispose()
	{
		denseValues = Array.Empty<T>();
		denseToSparse = Array.Empty<int>();
		sparseSlots.Clear();
		denseCount = 0;
		freeHead = -1;
	}

	private void EnsureDenseCapacity(int capacity)
	{
		if (capacity <= denseValues.Length)
		{
			return;
		}

		var newCapacity = 1;
		while (newCapacity < capacity)
		{
			newCapacity <<= 1;
		}

		Array.Resize(ref denseValues, newCapacity);
		Array.Resize(ref denseToSparse, newCapacity);
	}

	private bool TryGetSparseSlot(GenerationalHandle handle, out SparseSlot slot)
	{
		if (!handle.IsValid || handle.Index > int.MaxValue || handle.Index >= (uint)sparseSlots.Count)
		{
			slot = default;
			return false;
		}

		slot = sparseSlots[(int)handle.Index];
		return slot.Occupied != 0 && slot.Generation == handle.Generation;
	}

	private static uint NextGeneration(uint generation)
	{
		generation += 1;
		return generation == 0 ? 1u : generation;
	}
}
