using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

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
		// The view's draws go in row Row of the outputs.
		public int Row;
		// Batches draw only with all of these BatchFlags.
		public int RequiredFlags;
		// Batches with any of these BatchFlags are left out of the row's draws.
		public int ExcludedFlags;
	}

	/// <summary>
	/// A batch to submit and the bounds of its instances that a view sees.
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct BatchDraw
	{
		public Bounds Bounds;
		// -1 for row bounds with no batches.
		public int BatchIndex;
	}

	/// <summary>
	/// For each view, finds the batches with instances in a cell that passes
	/// the view's inward-facing planes, its sphere and the batch's LOD
	/// distances scaled by LodScale from LodOrigin, with the bounds of the
	/// batch's entries that pass. Of those with the view's RequiredFlags, it
	/// writes the ones without its ExcludedFlags to Draws, compacted in batch
	/// order from Row * BatchCount, with their count in DrawCounts[Row], and
	/// unites all of them, excluded or not, into RowBounds[Row], under the first
	/// one's index.
	/// </summary>
	[BurstCompile]
	public struct MarkVisibleBatchesJob : IJobParallelFor
	{
		[ReadOnly] public NativeArray<BatchCullCell> Cells;
		[ReadOnly] public NativeArray<BatchCullEntry> Entries;
		[ReadOnly] public NativeArray<BatchCullView> Views;
		[ReadOnly] public NativeArray<float4> Planes;
		[ReadOnly] public NativeArray<byte> BatchFlags;
		[ReadOnly] public int CellCount;
		[ReadOnly] public int BatchCount;

		// Views write disjoint rows.
		[NativeDisableParallelForRestriction] public NativeArray<BatchDraw> Draws;
		[NativeDisableParallelForRestriction] public NativeArray<int> DrawCounts;
		[NativeDisableParallelForRestriction] public NativeArray<BatchDraw> RowBounds;

		public void Execute(int viewIndex)
		{
			var view = Views[viewIndex];
			// A batch no entry passes keeps a minimum above its maximum.
			var boundsMinimum = new NativeArray<float3>(BatchCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
			var boundsMaximum = new NativeArray<float3>(BatchCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
			for (var batchIndex = 0; batchIndex < BatchCount; batchIndex += 1)
			{
				boundsMinimum[batchIndex] = float.MaxValue;
				boundsMaximum[batchIndex] = -float.MaxValue;
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

					var batch = entry.BatchIndex;
					boundsMinimum[batch] = math.min(boundsMinimum[batch], entry.BoundsMinimum);
					boundsMaximum[batch] = math.max(boundsMaximum[batch], entry.BoundsMaximum);
				}
			}

			var rowStart = view.Row * BatchCount;
			var drawCount = 0;
			var firstBatch = -1;
			var rowMinimum = new float3(float.MaxValue);
			var rowMaximum = new float3(-float.MaxValue);
			for (var batchIndex = 0; batchIndex < BatchCount; batchIndex += 1)
			{
				var minimum = boundsMinimum[batchIndex];
				var maximum = boundsMaximum[batchIndex];
				var flags = (int)BatchFlags[batchIndex];
				if (minimum.x > maximum.x || (flags & view.RequiredFlags) != view.RequiredFlags)
				{
					continue;
				}

				firstBatch = firstBatch < 0 ? batchIndex : firstBatch;
				rowMinimum = math.min(rowMinimum, minimum);
				rowMaximum = math.max(rowMaximum, maximum);
				if ((flags & view.ExcludedFlags) != 0)
				{
					continue;
				}

				Draws[rowStart + drawCount] = new BatchDraw
				{
					Bounds = ToBounds(minimum, maximum), BatchIndex = batchIndex,
				};
				drawCount += 1;
			}

			DrawCounts[view.Row] = drawCount;
			RowBounds[view.Row] = new BatchDraw
			{
				Bounds = firstBatch < 0 ? default : ToBounds(rowMinimum, rowMaximum), BatchIndex = firstBatch,
			};
		}

		private static Bounds ToBounds(float3 minimum, float3 maximum)
		{
			return new Bounds((minimum + maximum) * 0.5f, maximum - minimum);
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
}
