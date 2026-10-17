using System;
using UnityEngine;

namespace IndirectRendering.Collections;

internal sealed class GenerationalSlotAllocator
{
	private uint[] generations = [];
	private byte[] occupied = [];
	private int[] nextFree = [];
	private int freeHead = -1;

	internal GenerationalSlotAllocator(int capacity = 0)
	{
		if (capacity > 0)
		{
			EnsureCapacity(capacity);
		}
	}

	internal int HighWaterMark { get; private set; }
	internal int LiveCount { get; private set; }

	internal GenerationalHandle Allocate()
	{
		int index;
		if (freeHead >= 0)
		{
			index = freeHead;
			freeHead = nextFree[index];
		}
		else
		{
			index = HighWaterMark;
			EnsureCapacity(index + 1);
			HighWaterMark += 1;
			if (generations[index] == 0)
			{
				generations[index] = 1;
			}
		}

		occupied[index] = 1;
		nextFree[index] = -1;
		LiveCount += 1;
		return new GenerationalHandle((uint)index, generations[index]);
	}

	internal bool Release(GenerationalHandle handle)
	{
		return Release(handle, out _);
	}

	internal bool Release(GenerationalHandle handle, out int index)
	{
		if (!TryGetIndex(handle, out index))
		{
			return false;
		}

		occupied[index] = 0;
		generations[index] = NextGeneration(generations[index]);
		nextFree[index] = freeHead;
		freeHead = index;
		LiveCount -= 1;
		return true;
	}

	internal bool Contains(GenerationalHandle handle)
	{
		return TryGetIndex(handle, out _);
	}

	internal int GetSlotIndex(GenerationalHandle handle)
	{
		if (!TryGetIndex(handle, out var index))
		{
			throw new InvalidOperationException($"Unknown or stale slot handle: {handle}.");
		}

		return index;
	}

	internal void Clear()
	{
		freeHead = -1;
		LiveCount = 0;
		for (var index = 0; index < HighWaterMark; index += 1)
		{
			// Bump free slots too, so no stale handle becomes valid again after reuse.
			generations[index] = NextGeneration(generations[index]);
			occupied[index] = 0;
			nextFree[index] = -1;
		}

		HighWaterMark = 0;
	}

	private void EnsureCapacity(int capacity)
	{
		if (capacity <= generations.Length)
		{
			return;
		}

		var oldCapacity = generations.Length;
		var newCapacity = Mathf.NextPowerOfTwo(capacity);
		Array.Resize(ref generations, newCapacity);
		Array.Resize(ref occupied, newCapacity);
		Array.Resize(ref nextFree, newCapacity);
		for (var index = oldCapacity; index < newCapacity; index += 1)
		{
			nextFree[index] = -1;
		}
	}

	private bool TryGetIndex(GenerationalHandle handle, out int index)
	{
		if (!handle.IsValid || handle.Index > int.MaxValue)
		{
			index = -1;
			return false;
		}

		index = (int)handle.Index;
		return index < HighWaterMark && occupied[index] != 0 &&
			generations[index] == handle.Generation;
	}

	private static uint NextGeneration(uint generation)
	{
		generation += 1;
		return generation == 0 ? 1u : generation;
	}
}
