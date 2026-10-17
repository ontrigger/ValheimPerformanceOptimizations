using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using IndirectRendering.Burst;
using IndirectRendering.Extensions;

namespace IndirectRendering;

public sealed partial class IndirectRenderer
{
	// Row 0 of the visibility is the camera and row 1 the directional shadow
	// volume; view i's row is FixedViewCount + i, so rows index like the draws.
	private const int DirectionalShadowCullRow = 1;

	private BatchCellGrid batchCells;

	private NativeArray<BatchCullView> batchCullViews;
	private NativeArray<float4> batchCullPlanes;
	private NativeArray<int> shadowCullRows;
	private NativeArray<byte> batchVisibility;
	private NativeArray<float3> batchBoundsMinimum;
	private NativeArray<float3> batchBoundsMaximum;
	// The directional cascades and local shadow lights, which share one
	// shadow-casting draw per batch.
	private NativeArray<byte> shadowBatchVisibility;
	// The union of the batch's instance bounds in the cells the shadow views
	// see, which Unity culls each cascade and light against.
	private NativeArray<float3> shadowBatchBoundsMinimum;
	private NativeArray<float3> shadowBatchBoundsMaximum;
	private JobHandle batchCullHandle;
	private bool batchCullScheduled;
	// Zero when this frame's batches were not culled, so every batch draws;
	// batches added after the cull have no draws this frame.
	private int culledBatchCount;
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
	/// after the views are collected.
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
		batchCullViews.EnsureCapacity(cullViewCount);
		batchCullPlanes.EnsureCapacity(cameraFrustumPlanes.Length + MaxShadowCullingPlanes + views.Count * 6);
		shadowCullRows.EnsureCapacity(1 + views.Count);
		batchVisibility.EnsureCapacity((FixedViewCount + views.Count) * batchCount);
		batchBoundsMinimum.EnsureCapacity((FixedViewCount + views.Count) * batchCount);
		batchBoundsMaximum.EnsureCapacity((FixedViewCount + views.Count) * batchCount);
		shadowBatchVisibility.EnsureCapacity(batchCount);
		shadowBatchBoundsMinimum.EnsureCapacity(batchCount);
		shadowBatchBoundsMaximum.EnsureCapacity(batchCount);

		var lodOrigin = (Vector3)cameraLodParameters;
		var lodScale = cameraLodParameters.w;
		var planeCount = 0;
		var shadowRowCount = 0;
		if (!viewsOnly)
		{
			shadowCullRows[shadowRowCount] = DirectionalShadowCullRow * batchCount;
			shadowRowCount += 1;
			batchCullViews[0] = CreateCullView(
				cameraFrustumPlanes, cameraFrustumPlanes.Length, default, lodOrigin, lodScale, 0, ref planeCount);
			batchCullViews[1] = CreateCullView(
				shadowPlanes, shadowPlaneCount, default, lodOrigin, lodScale,
				DirectionalShadowCullRow * batchCount, ref planeCount);
		}

		for (var index = 0; index < views.Count; index += 1)
		{
			var view = views[index];
			var outputOffset = (FixedViewCount + index) * batchCount;
			var viewPlaneCount = view.HasFrustum ? ViewPlaneCount : 0;
			for (var plane = 0; plane < viewPlaneCount; plane += 1)
			{
				batchCullPlanes[planeCount + plane] = viewFrustumPlanes[index * ViewPlaneCount + plane];
			}

			batchCullViews[fixedCullViewCount + index] = new BatchCullView
			{
				Sphere = view.Sphere, LodOrigin = view.LodOrigin, LodScale = view.LodScale,
				PlaneStart = planeCount, PlaneCount = viewPlaneCount, OutputOffset = outputOffset,
			};
			planeCount += viewPlaneCount;
			if (view.ShadowCastersOnly)
			{
				// Local shadow lights draw through the shared shadow draws.
				shadowCullRows[shadowRowCount] = outputOffset;
				shadowRowCount += 1;
			}
		}

		batchCells.Pack(out var cells, out var entries);
		var markHandle = new MarkVisibleBatchesJob
		{
			Cells = cells, Entries = entries, Views = batchCullViews, Planes = batchCullPlanes,
			CellCount = batchCells.CellCount, BatchCount = batchCount, Visibility = batchVisibility,
			BoundsMinimum = batchBoundsMinimum, BoundsMaximum = batchBoundsMaximum,
		}.Schedule(cullViewCount, 1);
		// The shared shadow draws are only submitted by Render.
		batchCullHandle = viewsOnly ? markHandle : new MergeBatchVisibilityJob
		{
			Visibility = batchVisibility, BoundsMinimum = batchBoundsMinimum, BoundsMaximum = batchBoundsMaximum,
			RowOffsets = shadowCullRows, RowCount = shadowRowCount,
			Result = shadowBatchVisibility, ResultMinimum = shadowBatchBoundsMinimum,
			ResultMaximum = shadowBatchBoundsMaximum,
		}.Schedule(batchCount, 64, markHandle);
		JobHandle.ScheduleBatchedJobs();
		batchCullScheduled = true;
		culledBatchCount = batchCount;
	}

	private BatchCullView CreateCullView(
		Plane[] planes,
		int planeCount,
		Vector4 sphere,
		Vector3 lodOrigin,
		float lodScale,
		int outputOffset,
		ref int planeStart)
	{
		for (var index = 0; index < planeCount; index += 1)
		{
			var plane = planes[index];
			batchCullPlanes[planeStart + index] = new float4(plane.normal, plane.distance);
		}

		var view = new BatchCullView
		{
			Sphere = sphere, LodOrigin = lodOrigin, LodScale = lodScale,
			PlaneStart = planeStart, PlaneCount = planeCount, OutputOffset = outputOffset,
		};
		planeStart += planeCount;
		return view;
	}

	/// <summary>
	/// Waits for the batch cull; call before reading batch visibility.
	/// </summary>
	private void CompleteBatchCull()
	{
		if (!batchCullScheduled) { return; }

		batchCullHandle.Complete();
		batchCullScheduled = false;
	}

	private bool IsBatchVisible(int viewIndex, int batchIndex)
	{
		if (culledBatchCount == 0)
		{
			return true;
		}

		return batchIndex < culledBatchCount && batchVisibility[viewIndex * culledBatchCount + batchIndex] != 0;
	}

	private bool IsShadowBatchVisible(int batchIndex)
	{
		if (culledBatchCount == 0)
		{
			return true;
		}

		return batchIndex < culledBatchCount && shadowBatchVisibility[batchIndex] != 0;
	}

	/// <summary>
	/// The bounds of the batch's instances the shadow views can see, or its
	/// registered bounds when this frame's batches were not culled. Only valid
	/// for a batch IsShadowBatchVisible accepts.
	/// </summary>
	private Bounds GetShadowBatchBounds(int batchIndex, in Bounds registeredBounds)
	{
		if (culledBatchCount == 0)
		{
			return registeredBounds;
		}

		var bounds = default(Bounds);
		bounds.SetMinMax(shadowBatchBoundsMinimum[batchIndex], shadowBatchBoundsMaximum[batchIndex]);
		return bounds;
	}

	/// <summary>
	/// The bounds of the batch's instances in the cells the view sees, or its
	/// registered bounds when this frame's batches were not culled. Only valid
	/// for a batch IsBatchVisible accepts.
	/// </summary>
	private Bounds GetViewBatchBounds(int viewIndex, int batchIndex, in Bounds registeredBounds)
	{
		if (culledBatchCount == 0)
		{
			return registeredBounds;
		}

		var index = viewIndex * culledBatchCount + batchIndex;
		var bounds = default(Bounds);
		bounds.SetMinMax(batchBoundsMinimum[index], batchBoundsMaximum[index]);
		return bounds;
	}

	private void DisposeBatchCulling()
	{
		CompleteBatchCull();
		culledBatchCount = 0;
		batchCullViews.DisposeIfCreated();
		batchCullPlanes.DisposeIfCreated();
		shadowCullRows.DisposeIfCreated();
		batchVisibility.DisposeIfCreated();
		batchBoundsMinimum.DisposeIfCreated();
		batchBoundsMaximum.DisposeIfCreated();
		shadowBatchVisibility.DisposeIfCreated();
		shadowBatchBoundsMinimum.DisposeIfCreated();
		shadowBatchBoundsMaximum.DisposeIfCreated();
		batchCells.Dispose();
	}
}
