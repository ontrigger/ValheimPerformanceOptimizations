using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace IndirectRendering.Burst
{
	[StructLayout(LayoutKind.Sequential)]
	public struct BatchCullCell
	{
		public float3 Center;
		public float3 Extents;
		public int EntryStart;
		public int EntryCount;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct BatchCullEntry
	{
		public int BatchIndex;
		public int InstanceCount;
		public float MinimumStartDistance;
		public float MaximumEndDistance;
		// The batch's instance bounds within the cell, without the LOD
		// reference points the cell bounds include.
		public float3 BoundsMinimum;
		public float3 BoundsMaximum;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct BatchCullView
	{
		// xyz center, w radius; w <= 0 disables the test.
		public float4 Sphere;
		public float3 LodOrigin;
		public float LodScale;
		public int PlaneStart;
		public int PlaneCount;
		public int OutputOffset;
	}

	/// <summary>
	/// For each view, writes Visibility[OutputOffset + batch] = 1 for every
	/// batch with instances in a cell that passes the view's inward-facing
	/// planes, its sphere and the batch's LOD distances scaled by LodScale from
	/// LodOrigin, and 0 for the other batches. BoundsMinimum and BoundsMaximum
	/// at the same index get the union of the batch's entry bounds that pass the
	/// view, or float.MaxValue and -float.MaxValue when none does.
	/// </summary>
	[BurstCompile]
	public struct MarkVisibleBatchesJob : IJobParallelFor
	{
		[ReadOnly] public NativeArray<BatchCullCell> Cells;
		[ReadOnly] public NativeArray<BatchCullEntry> Entries;
		[ReadOnly] public NativeArray<BatchCullView> Views;
		[ReadOnly] public NativeArray<float4> Planes;
		[ReadOnly] public int CellCount;
		[ReadOnly] public int BatchCount;

		// Views write disjoint rows.
		[NativeDisableParallelForRestriction] public NativeArray<byte> Visibility;
		[NativeDisableParallelForRestriction] public NativeArray<float3> BoundsMinimum;
		[NativeDisableParallelForRestriction] public NativeArray<float3> BoundsMaximum;

		public void Execute(int viewIndex)
		{
			var view = Views[viewIndex];
			for (var batchIndex = 0; batchIndex < BatchCount; batchIndex += 1)
			{
				Visibility[view.OutputOffset + batchIndex] = 0;
				BoundsMinimum[view.OutputOffset + batchIndex] = float.MaxValue;
				BoundsMaximum[view.OutputOffset + batchIndex] = -float.MaxValue;
			}

			var sphereCenter = view.Sphere.xyz;
			var sphereRadiusSquared = view.Sphere.w * view.Sphere.w;
			for (var cellIndex = 0; cellIndex < CellCount; cellIndex += 1)
			{
				var cell = Cells[cellIndex];
				if (!IntersectsView(view, sphereCenter, sphereRadiusSquared, cell.Center, cell.Extents))
				{
					continue;
				}

				var offsetToCenter = math.abs(cell.Center - view.LodOrigin);
				var minimumDistance = math.length(math.max(offsetToCenter - cell.Extents, 0f));
				var maximumDistance = math.length(offsetToCenter + cell.Extents);
				var entryEnd = cell.EntryStart + cell.EntryCount;
				for (var entryIndex = cell.EntryStart; entryIndex < entryEnd; entryIndex += 1)
				{
					var entry = Entries[entryIndex];
					// float.MaxValue scales (LOD0 only) overflow to infinity,
					// which keeps start distances above zero out of range.
					if (minimumDistance >= entry.MaximumEndDistance * view.LodScale ||
					    maximumDistance < entry.MinimumStartDistance * view.LodScale)
					{
						continue;
					}

					var entryCenter = (entry.BoundsMinimum + entry.BoundsMaximum) * 0.5f;
					var entryExtents = (entry.BoundsMaximum - entry.BoundsMinimum) * 0.5f;
					if (!IntersectsView(view, sphereCenter, sphereRadiusSquared, entryCenter, entryExtents))
					{
						continue;
					}

					var outputIndex = view.OutputOffset + entry.BatchIndex;
					Visibility[outputIndex] = 1;
					BoundsMinimum[outputIndex] = math.min(BoundsMinimum[outputIndex], entry.BoundsMinimum);
					BoundsMaximum[outputIndex] = math.max(BoundsMaximum[outputIndex], entry.BoundsMaximum);
				}
			}
		}

		private bool IntersectsView(
			BatchCullView view,
			float3 sphereCenter,
			float sphereRadiusSquared,
			float3 center,
			float3 extents)
		{
			var minimumOffset = math.max(math.abs(center - sphereCenter) - extents, 0f);
			if (view.Sphere.w > 0f && math.lengthsq(minimumOffset) > sphereRadiusSquared)
			{
				return false;
			}

			return IntersectsPlanes(center, extents, view.PlaneStart, view.PlaneCount);
		}

		private bool IntersectsPlanes(float3 center, float3 extents, int planeStart, int planeCount)
		{
			var planeEnd = planeStart + planeCount;
			for (var planeIndex = planeStart; planeIndex < planeEnd; planeIndex += 1)
			{
				var plane = Planes[planeIndex];
				var radius = math.dot(math.abs(plane.xyz), extents);
				if (math.dot(plane.xyz, center) + plane.w < -radius)
				{
					return false;
				}
			}

			return true;
		}
	}

	/// <summary>
	/// ORs the rows at RowOffsets into Result, one batch per index, and unions
	/// their bounds into ResultMinimum and ResultMaximum.
	/// </summary>
	[BurstCompile]
	public struct MergeBatchVisibilityJob : IJobParallelFor
	{
		[ReadOnly] public NativeArray<byte> Visibility;
		[ReadOnly] public NativeArray<float3> BoundsMinimum;
		[ReadOnly] public NativeArray<float3> BoundsMaximum;
		[ReadOnly] public NativeArray<int> RowOffsets;
		[ReadOnly] public int RowCount;

		[WriteOnly] public NativeArray<byte> Result;
		[WriteOnly] public NativeArray<float3> ResultMinimum;
		[WriteOnly] public NativeArray<float3> ResultMaximum;

		public void Execute(int batchIndex)
		{
			byte visible = 0;
			var minimum = new float3(float.MaxValue);
			var maximum = new float3(-float.MaxValue);
			for (var row = 0; row < RowCount; row += 1)
			{
				var index = RowOffsets[row] + batchIndex;
				visible |= Visibility[index];
				minimum = math.min(minimum, BoundsMinimum[index]);
				maximum = math.max(maximum, BoundsMaximum[index]);
			}

			Result[batchIndex] = visible;
			ResultMinimum[batchIndex] = minimum;
			ResultMaximum[batchIndex] = maximum;
		}
	}
}
