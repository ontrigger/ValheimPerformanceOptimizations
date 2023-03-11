using System;
using System.Collections.Generic;

namespace ValheimPerformanceOptimizations.Storage
{
	public class ObjectPool<T> where T : new()
	{
		private readonly Stack<T> pool = new();

		private readonly Action<T> onRetrieve;
		private readonly Action<T> onReturn;

		public ObjectPool(Action<T> onRetrieve = null, Action<T> onReturn = null)
		{
			this.onRetrieve = onRetrieve;
			this.onReturn = onReturn;
		}

		public T Get()
		{
			var element = pool.Count == 0 ? new T() : pool.Pop();
			onRetrieve?.Invoke(element);

			return element;
		}

		public void Return(T toReturn)
		{
			onReturn?.Invoke(toReturn);

			pool.Push(toReturn);
		}
	}
}
