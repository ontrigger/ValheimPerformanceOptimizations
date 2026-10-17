using UnityEngine;

namespace IndirectRendering.Extensions
{
	public static class BoundsX
	{
		// The axis-aligned bounds of bounds after an affine transform.
		public static Bounds TransformBy(this Bounds bounds, Matrix4x4 matrix)
		{
			var extents = bounds.extents;
			return new Bounds(
				matrix.MultiplyPoint3x4(bounds.center),
				new Vector3(
					(Mathf.Abs(matrix.m00) * extents.x + Mathf.Abs(matrix.m01) * extents.y
					+ Mathf.Abs(matrix.m02) * extents.z) * 2f,
					(Mathf.Abs(matrix.m10) * extents.x + Mathf.Abs(matrix.m11) * extents.y
					+ Mathf.Abs(matrix.m12) * extents.z) * 2f,
					(Mathf.Abs(matrix.m20) * extents.x + Mathf.Abs(matrix.m21) * extents.y
					+ Mathf.Abs(matrix.m22) * extents.z) * 2f));
		}
	}
}
