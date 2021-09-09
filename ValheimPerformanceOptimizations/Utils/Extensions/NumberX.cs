using System;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Extensions
{
    public static class NumberX
    {
        public static bool IsNearlyEqual(this float x, float y)
        {
            return Mathf.Approximately(x, y);
        }
    }
}