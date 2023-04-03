using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

public class SparseDictionary<T> : IEnumerable<T>
{
	public int Count { get; private set; }

	public T[] Values => values;
	public int[] Keys => keys;

	private readonly int[] sparse;
	private readonly int[] keys;
	private readonly T[] values;

	public SparseDictionary(int maxVal, int capacity)
	{
		sparse = Enumerable.Repeat(-1, maxVal + 1).ToArray();
		keys = Enumerable.Repeat(-1, capacity).ToArray();
		values = Enumerable.Repeat<T>(default, capacity).ToArray();
	}

	IEnumerator IEnumerable.GetEnumerator()
	{
		return GetEnumerator();
	}

	public IEnumerator<T> GetEnumerator()
	{
		return Values.Take(Count).GetEnumerator();
	}

	public T this[int id] { set => Add(id, value); }

	public int Add(int valueId, T value)
	{
		if (valueId >= sparse.Length)
		{
			throw new Exception("Item is greater than max value.");
		}

		if (Count >= Values.Length)
		{
			throw new Exception("Set reached its capacity.");
		}

		sparse[valueId] = Count;
		Values[Count] = value;
		Keys[Count] = valueId;
		Count += 1;

		return Count;
	}

	public int Remove(int valueId)
	{
		if (valueId >= sparse.Length) { throw new Exception("Item is greater than max value."); }

		if (!Contains(valueId)) { throw new Exception("Item do not exist."); }

		var index = sparse[valueId];
		sparse[valueId] = -1;

		var lastVal = Values[Count - 1];
		Values[index] = lastVal;
		Values[Count - 1] = default;

		var lastValId = Keys[Count - 1];
		Keys[index] = lastValId;
		Keys[Count - 1] = -1;

		sparse[lastValId] = index;

		Count -= 1;

		return index;
	}

	public bool TryGetValue(int valueId, out T value)
	{
		var index = sparse[valueId];
		if (index != -1 && keys[index] == valueId)
		{
			value = values[index];
			return true;
		}
		value = default;
		return false;
	}

	public bool Contains(int valueId)
	{
		var index = sparse[valueId];
		return index != -1 && Keys[index] == valueId;
	}

	public void Clear()
	{
		Count = 0;
	}
}
