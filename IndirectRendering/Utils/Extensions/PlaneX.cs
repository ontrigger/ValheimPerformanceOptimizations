using UnityEngine;

namespace IndirectRendering.Extensions
{
	public static class PlaneX
	{
		/// <summary>
		/// The plane as (normal, distance), the layout shaders take planes in.
		/// </summary>
		public static Vector4 ToVector4(this Plane plane)
		{
			return new Vector4(plane.normal.x, plane.normal.y, plane.normal.z, plane.distance);
		}

		public static void EncodeTo(this Plane[] planes, Vector4[] destination, int planeCount)
		{
			for (var index = 0; index < planeCount; index += 1)
			{
				destination[index] = planes[index].ToVector4();
			}
		}
	}
}
