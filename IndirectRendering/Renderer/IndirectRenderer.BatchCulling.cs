using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using IndirectRendering.Burst;
using IndirectRendering.Extensions;

namespace IndirectRendering;

public sealed partial class IndirectRenderer
{
	// Row 0 of the draws is the camera and row 1 the directional shadow
	// volume; view i's row is FixedViewCount + i, so rows index like the draws.
	private const int DirectionalShadowCullRow = 1;

	private const int ShadowCasterCullFlag = 1;
	private const int MultiDrawCasterCullFlag = 2;

	private BatchCellGrid batchCells;

	private NativeArray<BatchCullView> batchCullViews;
	private NativeArray<float4> batchCullPlanes;
	private NativeArray<byte> batchCullFlags;
	private byte[] batchCullFlagsSource = [];
	// The lowest batch whose flags changed or that was added since the last
	// upload; see MarkBatchCullFlagsDirty.
	private int batchCullFlagsDirtyStart;
	private NativeArray<BatchDraw> batchDraws;
	private NativeArray<int> batchDrawCounts;
	private NativeArray<BatchDraw> rowBounds;
	private JobHandle batchCullHandle;
	private bool batchCullScheduled;
	// Zero when this frame's batches were not culled, so nothing draws;
	// batches added after the cull have no draws this frame.
	private int culledBatchCount;
	private int culledRowCount;
	private BatchDraw[] viewDrawsScratch = [];
#if DEBUG
	private int cameraSubmissionCount;
#endif

	private static Vector3 GetInstancePosition(in Matrix4x4 objectToWorld)
	{
		return new Vector3(objectToWorld.m03, objectToWorld.m13, objectToWorld.m23);
	}

	/// <summary>
	/// Starts culling the batches for the camera, the directional shadows and
	/// the collected views, or with viewsOnly, the collected views alone. Call
	/// after the views are collected. While multiDrawActive, the directional shadows
	/// and the shadowed lights' views leave their multi-draw casters out of
	/// their draws; see PrepareMultiDrawCasters.
	/// </summary>
	private void ScheduleBatchCull(bool viewsOnly = false)
	{
		CompleteBatchCull();
		culledBatchCount = 0;
		var batchCount = runtimeBatches.Count;
		if (batchCount == 0)
		{
			return;
		}

		var fixedCullViewCount = viewsOnly ? 0 : 2;
		var cullViewCount = fixedCullViewCount + views.Count;
		var rowCount = FixedViewCount + views.Count;
		batchCullViews.EnsureCapacity(cullViewCount);
		batchCullPlanes.EnsureCapacity(cameraFrustumPlanes.Length + MaxShadowCullingPlanes + views.Count * 6);
		batchDraws.EnsureCapacity(rowCount * batchCount);
		batchDrawCounts.EnsureCapacity(rowCount);
		rowBounds.EnsureCapacity(rowCount);
		UploadBatchCullFlags(batchCount);
		for (var row = 0; row < FixedViewCount; row += 1)
		{
			batchDrawCounts[row] = 0;
			rowBounds[row] = new BatchDraw { BatchIndex = -1 };
		}

		var excludedFlags = multiDrawActive ? MultiDrawCasterCullFlag : 0;
		var lodOrigin = (Vector3)cameraLodParameters;
		var lodScale = cameraLodParameters.w;
		var planeCount = 0;
		if (!viewsOnly)
		{
			batchCullViews[0] = CreateCullView(
				cameraFrustumPlanes, cameraFrustumPlanes.Length, lodOrigin, lodScale, 0, 0, 0, ref planeCount);
			batchCullViews[1] = CreateCullView(
				shadowPlanes, shadowPlaneCount, lodOrigin, lodScale, DirectionalShadowCullRow,
				ShadowCasterCullFlag, excludedFlags, ref planeCount);
		}

		for (var index = 0; index < views.Count; index += 1)
		{
			var view = views[index];
			var viewPlaneCount = view.HasFrustum ? ViewPlaneCount : 0;
			for (var plane = 0; plane < viewPlaneCount; plane += 1)
			{
				batchCullPlanes[planeCount + plane] = viewFrustumPlanes[index * ViewPlaneCount + plane];
			}

			var isShadowedLight = !viewsOnly && index < shadowedLights.Count;
			batchCullViews[fixedCullViewCount + index] = new BatchCullView
			{
				Sphere = view.Sphere, LodOrigin = view.LodOrigin, LodScale = view.LodScale,
				PlaneStart = planeCount, PlaneCount = viewPlaneCount, Row = FixedViewCount + index,
				RequiredFlags = view.ShadowCastersOnly ? ShadowCasterCullFlag : 0,
				ExcludedFlags = isShadowedLight ? excludedFlags : 0,
			};
			planeCount += viewPlaneCount;
		}

		batchCells.Pack(out var cells, out var entries);
		batchCullHandle = new MarkVisibleBatchesJob
		{
			Cells = cells, Entries = entries, Views = batchCullViews, Planes = batchCullPlanes,
			BatchFlags = batchCullFlags, CellCount = batchCells.CellCount, BatchCount = batchCount,
			Draws = batchDraws, DrawCounts = batchDrawCounts, RowBounds = rowBounds,
		}.Schedule(cullViewCount, 1);
		JobHandle.ScheduleBatchedJobs();
		batchCullScheduled = true;
		culledBatchCount = batchCount;
		culledRowCount = rowCount;
	}

	private void MarkBatchCullFlagsDirty(int batchIndex)
	{
		batchCullFlagsDirtyStart = Mathf.Min(batchCullFlagsDirtyStart, batchIndex);
	}

	private void UploadBatchCullFlags(int batchCount)
	{
		var start = batchCullFlagsDirtyStart;
		if (!batchCullFlags.IsCreated || batchCullFlags.Length < batchCount)
		{
			start = 0;
			batchCullFlags.EnsureCapacity(batchCount);
		}

		if (start >= batchCount) { return; }

		if (batchCullFlagsSource.Length < batchCount)
		{
			Array.Resize(ref batchCullFlagsSource, Mathf.NextPowerOfTwo(batchCount));
		}

		for (var batchIndex = start; batchIndex < batchCount; batchIndex += 1)
		{
			var runtimeBatch = runtimeBatches[batchIndex];
			batchCullFlagsSource[batchIndex] = (byte)(
				(runtimeBatch.ShadowEligible ? ShadowCasterCullFlag : 0) |
				(runtimeBatch.MultiDrawCaster ? MultiDrawCasterCullFlag : 0));
		}

		NativeArray<byte>.Copy(batchCullFlagsSource, start, batchCullFlags, start, batchCount - start);
		batchCullFlagsDirtyStart = batchCount;
	}

	private BatchCullView CreateCullView(
		Plane[] planes,
		int planeCount,
		Vector3 lodOrigin,
		float lodScale,
		int row,
		int requiredFlags,
		int excludedFlags,
		ref int planeStart)
	{
		for (var index = 0; index < planeCount; index += 1)
		{
			var plane = planes[index];
			batchCullPlanes[planeStart + index] = new float4(plane.normal, plane.distance);
		}

		var view = new BatchCullView
		{
			LodOrigin = lodOrigin, LodScale = lodScale, PlaneStart = planeStart, PlaneCount = planeCount,
			Row = row, RequiredFlags = requiredFlags, ExcludedFlags = excludedFlags,
		};
		planeStart += planeCount;
		return view;
	}

	/// <summary>
	/// Waits for the batch cull; call before reading its draws.
	/// </summary>
	private void CompleteBatchCull()
	{
		if (!batchCullScheduled) { return; }

		batchCullHandle.Complete();
		batchCullScheduled = false;
	}

	/// <summary>
	/// Copies the view's draws out of the batch cull into draws, growing it,
	/// and returns their count; reading them one by one from managed code
	/// costs more than the copy.
	/// </summary>
	private int CopyViewDraws(int viewIndex, ref BatchDraw[] draws)
	{
		if (culledBatchCount == 0 || viewIndex >= culledRowCount) { return 0; }

		var count = batchDrawCounts[viewIndex];
		if (draws.Length < count)
		{
			draws = new BatchDraw[Mathf.NextPowerOfTwo(count)];
		}

		if (count != 0)
		{
			NativeArray<BatchDraw>.Copy(batchDraws, viewIndex * culledBatchCount, draws, 0, count);
		}

		return count;
	}

	/// <summary>
	/// The union of the bounds of the view's visible batches with its required
	/// flags, whether excluded from its draws or not; BatchIndex is the first
	/// such batch, or -1 when there are none.
	/// </summary>
	private BatchDraw GetRowBounds(int viewIndex)
	{
		return culledBatchCount == 0 || viewIndex >= culledRowCount
			? new BatchDraw { BatchIndex = -1 }
			: rowBounds[viewIndex];
	}

	private void DisposeBatchCulling()
	{
		CompleteBatchCull();
		culledBatchCount = 0;
		batchCullViews.DisposeIfCreated();
		batchCullPlanes.DisposeIfCreated();
		batchCullFlags.DisposeIfCreated();
		batchDraws.DisposeIfCreated();
		batchDrawCounts.DisposeIfCreated();
		rowBounds.DisposeIfCreated();
		batchCells.Dispose();
	}
}
