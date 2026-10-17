using System;
using System.Collections.Generic;

namespace ValheimPerformanceOptimizations.Collections;

/// <summary>
/// Allocates variable-sized contiguous ranges from a monotonically growing
/// address space. Freed neighbours are coalesced and trailing free space is
/// returned to the high-water mark.
/// </summary>
internal sealed class ContiguousRangeAllocator
{
	private struct FreeRange
	{
		internal int Start;
		internal int Length;
	}

	private readonly List<FreeRange> _freeRanges = new();

	internal int HighWaterMark { get; private set; }

	internal int Allocate(int length)
	{
		if (length <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(length));
		}

		var bestIndex = -1;
		var bestLength = int.MaxValue;
		for (var index = 0; index < _freeRanges.Count; index++)
		{
			var candidateLength = _freeRanges[index].Length;
			if (candidateLength < length || candidateLength >= bestLength)
			{
				continue;
			}

			bestIndex = index;
			bestLength = candidateLength;
			if (candidateLength == length)
			{
				break;
			}
		}

		if (bestIndex >= 0)
		{
			var range = _freeRanges[bestIndex];
			var start = range.Start;
			if (range.Length == length)
			{
				_freeRanges.RemoveAt(bestIndex);
			}
			else
			{
				range.Start += length;
				range.Length -= length;
				_freeRanges[bestIndex] = range;
			}

			return start;
		}

		var allocatedStart = HighWaterMark;
		HighWaterMark = checked(HighWaterMark + length);
		return allocatedStart;
	}

	internal void Free(int start, int length)
	{
		if (start < 0 || length <= 0 || start > HighWaterMark - length)
		{
			throw new ArgumentOutOfRangeException();
		}

		var insertionIndex = 0;
		while (insertionIndex < _freeRanges.Count && _freeRanges[insertionIndex].Start < start)
		{
			insertionIndex++;
		}

		if (insertionIndex > 0)
		{
			var previous = _freeRanges[insertionIndex - 1];
			if (previous.Start + previous.Length > start)
			{
				throw new InvalidOperationException("Freed range overlaps an existing free range.");
			}
		}

		if (insertionIndex < _freeRanges.Count && start + length > _freeRanges[insertionIndex].Start)
		{
			throw new InvalidOperationException("Freed range overlaps an existing free range.");
		}

		_freeRanges.Insert(insertionIndex, new FreeRange { Start = start, Length = length });
		CoalesceAt(insertionIndex);
		TrimHighWaterMark();
	}

	internal void Clear()
	{
		_freeRanges.Clear();
		HighWaterMark = 0;
	}

	[System.Diagnostics.Conditional("DEBUG")]
	internal void Validate()
	{
		var previousEnd = 0;
		for (var index = 0; index < _freeRanges.Count; index++)
		{
			var range = _freeRanges[index];
			if (range.Start < previousEnd || range.Length <= 0 || range.Start + range.Length > HighWaterMark)
			{
				throw new InvalidOperationException("Contiguous range allocator free list is invalid.");
			}

			previousEnd = range.Start + range.Length;
		}
	}

	private void CoalesceAt(int index)
	{
		if (index > 0)
		{
			var previous = _freeRanges[index - 1];
			var current = _freeRanges[index];
			if (previous.Start + previous.Length == current.Start)
			{
				previous.Length += current.Length;
				_freeRanges[index - 1] = previous;
				_freeRanges.RemoveAt(index);
				index--;
			}
		}

		if (index + 1 >= _freeRanges.Count)
		{
			return;
		}

		var range = _freeRanges[index];
		var next = _freeRanges[index + 1];
		if (range.Start + range.Length == next.Start)
		{
			range.Length += next.Length;
			_freeRanges[index] = range;
			_freeRanges.RemoveAt(index + 1);
		}
	}

	private void TrimHighWaterMark()
	{
		while (_freeRanges.Count != 0)
		{
			var lastIndex = _freeRanges.Count - 1;
			var range = _freeRanges[lastIndex];
			if (range.Start + range.Length != HighWaterMark)
			{
				return;
			}

			HighWaterMark = range.Start;
			_freeRanges.RemoveAt(lastIndex);
		}
	}
}
