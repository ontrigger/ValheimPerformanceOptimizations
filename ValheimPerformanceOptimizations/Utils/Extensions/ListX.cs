using System.Collections.Generic;

namespace ValheimPerformanceOptimizations.Extensions
{
	public static class ListX
	{
		public static void RemoveAtSwapBack<T>(this List<T> list, int index)
		{
			var lastIndex = list.Count - 1;
			list[index] = list[lastIndex];
			list.RemoveAt(lastIndex);
		}

		public static bool RemoveSwapBack<T>(this List<T> list, T item)
		{
			var index = list.IndexOf(item);
			if (index < 0)
			{
				return false;
			}

			list.RemoveAtSwapBack(index);
			return true;
		}
	}
}
