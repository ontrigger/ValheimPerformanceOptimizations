using System;
using System.Collections.Generic;
using UnityEngine;

namespace IndirectRendering;

/// <summary>
/// Sparse per-instance overrides, in a struct the game's shaders define. Most
/// instances have none; instances point at a slot through their instance data
/// and slot 0 means "use the material". Writes are rare, so only dirty slots
/// upload. Create one with InstanceOverrideStore<T>
/// </summary>
public abstract class InstanceOverrideStore : IDisposable
{
	private readonly Stack<int> freeSlots = new();
	private int highWaterMark = 1;

	internal abstract GraphicsBuffer Buffer { get; }

	internal int Allocate()
	{
		if (freeSlots.Count != 0)
		{
			return freeSlots.Pop();
		}

		var index = highWaterMark;
		highWaterMark += 1;
		return index;
	}

	internal void Release(int index)
	{
		if (index <= 0)
		{
			return;
		}

		ClearSlot(index);
		freeSlots.Push(index);
	}

	internal bool TryUpload()
	{
		return Upload(highWaterMark);
	}

	internal void Reset()
	{
		freeSlots.Clear();
		highWaterMark = 1;
		ClearSlots();
	}

	public void Dispose()
	{
		Reset();
		DisposeBuffer();
	}

	private protected abstract void ClearSlot(int index);
	private protected abstract bool Upload(int count);
	private protected abstract void ClearSlots();
	private protected abstract void DisposeBuffer();
}
/// <summary>
/// T must match the shader's override struct, including its padding.
/// </summary>
public sealed class InstanceOverrideStore<T> : InstanceOverrideStore where T : struct
{
	private readonly StagedGraphicsBuffer<T> overrides = new(64);

	internal override GraphicsBuffer Buffer => overrides.Buffer;

	internal void Update(int index, in T data)
	{
		overrides.Set(index, data);
	}

	private protected override void ClearSlot(int index)
	{
		overrides.Set(index, default);
	}

	private protected override bool Upload(int count)
	{
		return overrides.Upload(count);
	}

	private protected override void ClearSlots()
	{
		overrides.Clear();
	}

	private protected override void DisposeBuffer()
	{
		overrides.Dispose();
	}
}
