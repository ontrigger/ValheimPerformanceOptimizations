using System;
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

		public static bool IntersectsSphere(this Bounds bounds, Vector3 center, float radius)
		{
			if (radius == 0)
			{
				return bounds.Contains(center);
			}

			var min = bounds.center - bounds.extents;
			var max = bounds.center + bounds.extents;

			double ex = Math.Max(min.x - center.x, 0) + Math.Max(center.x - max.x, 0);
			double ey = Math.Max(min.y - center.y, 0) + Math.Max(center.y - max.y, 0);
			double ez = Math.Max(min.z - center.z, 0) + Math.Max(center.z - max.z, 0);

			return ex < radius && ey < radius && ez < radius && ex * ex + ey * ey + ez * ez < radius * radius;
		}
	}
}
