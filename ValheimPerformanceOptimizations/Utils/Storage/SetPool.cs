using System.Collections.Generic;

namespace ValheimPerformanceOptimizations.Storage
{
	public static class SetPool<T>
	{
		private static readonly ObjectPool<HashSet<T>> Pool = new(onReturn: Clear);

		public static HashSet<T> Get()
		{
			return Pool.Get();
		}

		public static void Return(HashSet<T> toReturn)
		{
			Pool.Return(toReturn);
		}

		private static void Clear(HashSet<T> set)
		{
			set.Clear();
		}
	}
}
