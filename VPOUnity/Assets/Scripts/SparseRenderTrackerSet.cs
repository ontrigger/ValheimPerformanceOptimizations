using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

public class SparseSet<T> : IEnumerable<T> where T : IHasID
{
	public int Count { get; private set; }

	public T[] Values { get; }

	private readonly int[] sparse;

	public SparseSet(int maxVal, int capacity)
	{
		sparse = Enumerable.Repeat(-1, maxVal + 1).ToArray();
		Values = Enumerable.Repeat<T>(default, capacity).ToArray();
	}
	
	IEnumerator IEnumerable.GetEnumerator()
	{
		return GetEnumerator();
	}

	public IEnumerator<T> GetEnumerator()
	{
		return Values.Take(Count).GetEnumerator();
	}
	
	public void Add(T value)
	{
		if (value.ID >= sparse.Length) throw new Exception("Item is greater than max value.");

		if (Count >= Values.Length) throw new Exception("Set reached its capacity.");

		sparse[value.ID] = Count;
		Values[Count] = value;
		Count++;
	}

	public void Remove(T value)
	{
		if (value.ID >= sparse.Length) throw new Exception("Item is greater than max value.");

		if (Contains(value) == false) throw new Exception("Item do not exist.");

		var index = sparse[value.ID];
		sparse[value.ID] = -1;

		var lastVal = Values[Count - 1];
		Values[index] = lastVal;
		Values[Count - 1] = default;

		sparse[lastVal.ID] = index;

		Count--;
	}

	public bool Contains(T value)
	{
		var index = sparse[value.ID];
		return index != -1 && Values[index].ID == value.ID;
	}

	public void Clear()
	{
		Count = 0;
	}
}
