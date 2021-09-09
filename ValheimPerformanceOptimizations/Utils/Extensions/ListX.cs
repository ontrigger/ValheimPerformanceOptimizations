using System.Collections.Generic;

namespace ValheimPerformanceOptimizations.Extensions
{
    public static class ListX
    {
        public static void RemoveBySwap<T>(this List<T> list, int index)
        {
            list[index] = list[list.Count - 1];
            list.RemoveAt(list.Count - 1);
        }
    }
}