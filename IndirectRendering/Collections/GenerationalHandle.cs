using System;
using System.Runtime.InteropServices;

namespace IndirectRendering.Collections;

/// <summary>
/// Stable identity for an entry in a generational collection.
/// </summary>
/// <remarks>
/// The index is owned by the collection that created the handle. It is not a
/// dense iteration index and must not be used as one by callers.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct GenerationalHandle : IEquatable<GenerationalHandle>
{
	public static readonly GenerationalHandle Invalid = default;

	internal readonly uint Index;
	internal readonly uint Generation;

	internal GenerationalHandle(uint index, uint generation)
	{
		Index = index;
		Generation = generation;
	}

	public bool IsValid => Generation != 0;

	public bool Equals(GenerationalHandle other)
	{
		return Index == other.Index && Generation == other.Generation;
	}

	public override bool Equals(object obj)
	{
		return obj is GenerationalHandle other && Equals(other);
	}

	public override int GetHashCode()
	{
		unchecked
		{
			return ((int)Index * 397) ^ (int)Generation;
		}
	}

	public override string ToString()
	{
		return IsValid ? $"{Index}:{Generation}" : "Invalid";
	}

	public static bool operator ==(GenerationalHandle left, GenerationalHandle right)
	{
		return left.Equals(right);
	}

	public static bool operator !=(GenerationalHandle left, GenerationalHandle right)
	{
		return !left.Equals(right);
	}
}
