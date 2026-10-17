using UnityEngine;

namespace IndirectRendering
{
	public static class ComputeDispatch
	{
		private const int MaximumGroupsPerDimension = 65535;

		public static int GetThreadGroupCount(int threadCount, int groupSize)
		{
			return (threadCount + groupSize - 1) / groupSize;
		}

		/// <summary>
		/// Spreads groupCount thread groups over X and Y, since one dimension
		/// holds at most 65535.
		/// </summary>
		public static void GetDimensions(int groupCount, out int dispatchX, out int dispatchY)
		{
			var safeGroupCount = Mathf.Max(1, groupCount);
			dispatchX = Mathf.Min(safeGroupCount, MaximumGroupsPerDimension);
			dispatchY = (int)(((long)safeGroupCount + MaximumGroupsPerDimension - 1) /
				MaximumGroupsPerDimension);
		}
	}
}
