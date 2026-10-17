using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;

namespace IndirectRendering
{
	/// <summary>
	/// A structured GraphicsBuffer with a CPU copy. Writes mark their slot dirty
	/// and Upload sends only the dirty slots, merged into ranges, unless the
	/// buffer has to grow.
	/// </summary>
	internal sealed class StagedGraphicsBuffer<T> : IDisposable where T : struct
	{
		private const int MaxMergedGapBytes = 16 * 1024;
		// Dirty bits are scanned a word at a time, so gaps inside a word always
		// merge; never split ranges more finely than that.
		private static readonly int MaxMergedGapSlots = Math.Max(64, MaxMergedGapBytes / Marshal.SizeOf<T>());

		// (start, count) pairs, reused between uploads.
		private readonly List<int> ranges = new();
		private T[] values;
		// One bit per slot, scanned in slot order so ranges need no sort; a mass
		// unregister dirties tens of thousands of slots.
		private ulong[] dirtyBits;
		private int dirtyMinWord = int.MaxValue;
		private int dirtyMaxWord = -1;
		private int bufferCapacity;

		internal StagedGraphicsBuffer(int capacity)
		{
			values = new T[capacity];
			dirtyBits = new ulong[WordCount(capacity)];
		}

		internal GraphicsBuffer Buffer { get; private set; }

		internal ref readonly T this[int index] => ref values[index];

		internal void Set(int index, in T value)
		{
			EnsureCapacity(index + 1);
			values[index] = value;
			var word = index >> 6;
			dirtyBits[word] |= 1UL << (index & 63);
			dirtyMinWord = Math.Min(dirtyMinWord, word);
			dirtyMaxWord = Math.Max(dirtyMaxWord, word);
		}

		/// <summary>
		/// Uploads the dirty slots, or slots [0, count) into a new buffer when the
		/// current one holds fewer than count. count must cover every written
		/// slot. Returns whether the buffer was replaced, which invalidates
		/// bindings to the old one.
		/// </summary>
		internal bool Upload(int count)
		{
			if (Buffer == null || bufferCapacity < count)
			{
				Buffer?.Release();
				EnsureCapacity(count);
				bufferCapacity = Mathf.NextPowerOfTwo(Math.Max(count, 1));
				Buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferCapacity, Marshal.SizeOf<T>());
				Buffer.SetData(values, 0, 0, count);
				ClearDirtyBits();
				return true;
			}

			if (dirtyMaxWord < 0) { return false; }

			Profiler.BeginSample("CollectDirtyRanges");
			var rangeStart = -1;
			var rangeEnd = -1;
			for (var word = dirtyMinWord; word <= dirtyMaxWord; word += 1)
			{
				var bits = dirtyBits[word];
				if (bits == 0) { continue; }

				dirtyBits[word] = 0;
				var first = (word << 6) + math.tzcnt(bits);
				if (rangeStart < 0)
				{
					rangeStart = first;
				}
				else if (first - rangeEnd > MaxMergedGapSlots)
				{
					ranges.Add(rangeStart);
					ranges.Add(rangeEnd - rangeStart + 1);
					rangeStart = first;
				}

				rangeEnd = (word << 6) + 63 - math.lzcnt(bits);
			}

			ranges.Add(rangeStart);
			ranges.Add(rangeEnd - rangeStart + 1);
			dirtyMinWord = int.MaxValue;
			dirtyMaxWord = -1;
			Profiler.EndSample();

			Profiler.BeginSample("SetData");
			for (var index = 0; index < ranges.Count; index += 2)
			{
				Buffer.SetData(values, ranges[index], ranges[index], ranges[index + 1]);
			}
			Profiler.EndSample();

			ranges.Clear();
			return false;
		}

		/// <summary>
		/// Creates a one-slot buffer when there is none yet, so the buffer can be
		/// bound before anything is written. Returns whether it did.
		/// </summary>
		internal bool EnsureBuffer()
		{
			return Buffer == null && Upload(1);
		}

		internal void Clear()
		{
			Array.Clear(values, 0, values.Length);
			ClearDirtyBits();
			Buffer?.Release();
			Buffer = null;
			bufferCapacity = 0;
		}

		public void Dispose()
		{
			Clear();
			values = Array.Empty<T>();
			dirtyBits = Array.Empty<ulong>();
		}

		private void ClearDirtyBits()
		{
			if (dirtyMaxWord >= 0)
			{
				Array.Clear(dirtyBits, dirtyMinWord, dirtyMaxWord - dirtyMinWord + 1);
			}

			dirtyMinWord = int.MaxValue;
			dirtyMaxWord = -1;
		}

		private void EnsureCapacity(int capacity)
		{
			if (capacity <= values.Length) { return; }

			var newCapacity = Mathf.NextPowerOfTwo(capacity);
			Array.Resize(ref values, newCapacity);
			Array.Resize(ref dirtyBits, WordCount(newCapacity));
		}

		private static int WordCount(int capacity)
		{
			return (capacity + 63) >> 6;
		}
	}
}
