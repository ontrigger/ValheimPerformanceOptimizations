using UnityEngine;

namespace IndirectRendering.Extensions
{
	public static class VectorX
	{
		public static bool IsFinite(this Vector3 value)
		{
			return float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
		}
	}
}
