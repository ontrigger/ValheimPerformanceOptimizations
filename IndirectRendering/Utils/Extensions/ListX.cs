using System.Collections.Generic;

namespace IndirectRendering.Extensions
{
	public static class ListX
	{
		public static bool SequenceEquals(this List<int> list, List<int> other)
		{
			if (list.Count != other.Count) { return false; }

			for (var index = 0; index < other.Count; index += 1)
			{
				if (list[index] != other[index]) { return false; }
			}

			return true;
		}

		/// <summary>
		/// Whether list holds every item of subset; both are sorted ascending.
		/// </summary>
		public static bool ContainsSorted(this List<int> list, List<int> subset)
		{
			var listIndex = 0;
			for (var index = 0; index < subset.Count; index += 1)
			{
				var item = subset[index];
				while (listIndex < list.Count && list[listIndex] < item)
				{
					listIndex += 1;
				}

				if (listIndex == list.Count || list[listIndex] != item) { return false; }
			}

			return true;
		}
	}
}
