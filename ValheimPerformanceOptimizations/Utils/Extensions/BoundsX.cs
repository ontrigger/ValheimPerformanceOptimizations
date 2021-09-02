using UnityEngine;

namespace ValheimPerformanceOptimizations.Extensions
{
    public static class BoundsX
    {
        public static bool IntersectsXZ(this Bounds b1, Bounds b2)
        {
            return b1.min.x <= (double)b2.max.x && b1.max.x >= (double)b2.min.x &&
                   b1.min.z <= (double)b2.max.z && b1.max.z >= (double)b2.min.z;
        }
    }
}