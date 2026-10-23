using UnityEngine;

namespace IndirectRendering.Extensions
{
	public static class GraphicsBufferX
	{
		public static void Release(ref GraphicsBuffer buffer)
		{
			buffer?.Release();
			buffer = null;
		}
	}
}
