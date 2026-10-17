using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using IndirectRendering.Extensions;
using Debug = UnityEngine.Debug;

namespace IndirectRendering;

public sealed partial class IndirectRenderer
{
	private static readonly int ViewsId = Shader.PropertyToID("_Views");
	private static readonly int ViewCountId = Shader.PropertyToID("_ViewCount");
	private static readonly int VisibleStrideId = Shader.PropertyToID("_VisibleStride");
	private static readonly int ViewPositionId = Shader.PropertyToID("_VPOViewPosition");

	// Set on ViewData.Flags for views that only draw shadow casters.
	private const uint ViewShadowCastersOnlyFlag = 1u;
	// Set on ViewData.Flags for the first of a point light's six face views.
	private const uint ViewCubeFacesFlag = 2u;
	// Set on the camera's ViewData.Flags; see IsLargeEnoughUsingProjectedBounds.
	private const uint ViewScreenSizeFlag = 4u;

	[StructLayout(LayoutKind.Sequential)]
	private struct ViewData
	{
		internal Vector4 Plane0;
		internal Vector4 Plane1;
		internal Vector4 Plane2;
		internal Vector4 Plane3;
		internal Vector4 Plane4;
		internal Vector4 Plane5;
		internal Vector4 LodParameters;
		internal Vector4 Sphere;
		internal uint ViewIndex;
		internal uint Flags;
		private uint _padding0;
		private uint _padding1;
	}

	private struct QueuedView
	{
		internal IndirectView View;
		internal Action<int> Render;
	}

	// Requests run after this frame's culls; requests made while they run wait
	// for the next frame.
	private List<QueuedView> queuedViews = new();
	private List<QueuedView> runningViews = new();
	private bool viewsCollected;
	// The collected views: the shadowed lights', then the queued ones from
	// view index queuedViewStart.
	private readonly List<IndirectView> views = new();
	private int queuedViewStart = FixedViewCount;
	// The last frame Render ran; see CullViewsNow.
	private int renderedFrame = -1;

	private const int ViewPlaneCount = 6;

	private readonly Plane[] viewPlanes = new Plane[ViewPlaneCount];
	// The collected views' planes, six per view; a view without a frustum
	// has planes that pass everything.
	private Vector4[] viewFrustumPlanes = [];
	private ViewData[] viewData = [];
	private GraphicsBuffer viewDataBuffer;

	private readonly Dictionary<(Material, Shader), Material> replacementBySourceAndShader = new();
	private CommandBuffer replacementCommandBuffer;

	/// <summary>
	/// Culls the view with the next frame's culls, or this frame's if they have
	/// not run yet, then calls render with its view index once they have. The
	/// index is -1 when nothing was culled; the draw methods then draw nothing,
	/// so render still renders the rest of its view. While the renderer is
	/// disabled, render runs immediately with -1.
	/// </summary>
	public void QueueView(in IndirectView view, Action<int> render)
	{
		if (!Enabled)
		{
			render(-1);
			return;
		}

		queuedViews.Add(new QueuedView { View = view, Render = render });
	}

	/// <summary>
	/// Culls views at once, for a camera that renders before this frame's
	/// Render: view i is culled into view index first + i, for SubmitViewDraws,
	/// until the next CullViewsNow or Render. Returns first, or -1 when nothing
	/// was culled: while the renderer is disabled, or after this frame's
	/// Render, whose draws still need the view slots.
	/// </summary>
	public int CullViewsNow(ReadOnlySpan<IndirectView> requestedViews)
	{
		if (!Enabled || requestedViews.Length == 0 || renderedFrame == Time.frameCount)
		{
			return -1;
		}

		Profiler.BeginSample("CullViewsNow");
		// The attached shadow command buffers would prepare the last Render's
		// views from these slots in the camera's shadow passes, and a relayout
		// releases buffers they hold. Render attaches them again.
		DetachShadowCommandBuffers();
		// Render repeats these and finds nothing left to do.
		ApplyMovedInstances();
		UploadInstanceChanges();

		// Render collects its own views over these.
		views.Clear();
		foreach (var view in requestedViews)
		{
			views.Add(view);
		}

		CalculateViewFrustumPlanes();
		if (FixedViewCount + views.Count > viewCapacity)
		{
			viewCapacity = FixedViewCount + Mathf.NextPowerOfTwo(views.Count);
			layoutDirty = true;
		}

		EnsureBatchLayout();
		var instanceCount = instanceSlots.LiveCount != 0 ? instanceSlots.HighWaterMark : 0;
		if (instanceCount != 0)
		{
			ScheduleBatchCull(true);
			CompleteBatchCull();
			EnsureCullDiagnosticsBuffer();
			DispatchViewCull(default, instanceCount, true);
		}

		Profiler.EndSample();
		return instanceCount != 0 ? FixedViewCount : -1;
	}

	/// <summary>
	/// Draws a culled view into camera with each batch's own material, through
	/// Unity's renderer so the camera's lighting and shadows apply. Call before
	/// rendering the camera. A ShadowCastersOnly view draws into the camera's
	/// shadow maps only. Returns the number of draws submitted.
	/// </summary>
	public int SubmitViewDraws(int viewIndex, Camera camera)
	{
		if (viewIndex < 0) { return 0; }

		var viewSlot = viewIndex - FixedViewCount;
		var shadowsOnly = viewSlot >= 0 && viewSlot < views.Count && views[viewSlot].ShadowCastersOnly;
		var submissionCount = 0;
		for (var batchIndex = 0; batchIndex < runtimeBatches.Count; batchIndex += 1)
		{
			var runtimeBatch = runtimeBatches[batchIndex];
			if (runtimeBatch.RegisteredInstanceCount == 0 || !IsBatchVisible(viewIndex, batchIndex) ||
			    shadowsOnly && !runtimeBatch.ShadowEligible)
			{
				continue;
			}

			var renderParams = shadowsOnly ? runtimeBatch.ShadowRenderParams : runtimeBatch.CameraRenderParams;
			renderParams.worldBounds = GetViewBatchBounds(viewIndex, batchIndex, runtimeBatch.WorldBounds);
			renderParams.camera = camera;
			renderParams.matProps = GetViewMaterialProperties(runtimeBatch, viewIndex);
			Graphics.RenderMeshIndirect(
				renderParams,
				runtimeBatch.Mesh,
				indirectArgumentsBuffer,
				1,
				viewIndex * runtimeBatches.Count + batchIndex);
			submissionCount += 1;
		}

		return submissionCount;
	}

	/// <summary>
	/// The indirect counterpart of camera.RenderWithShader: draws a culled
	/// view into camera's target with pass 0 of shader, on copies of each
	/// batch's material, for batches on the camera's culling mask. Call after
	/// rendering the camera. shader reads the camera position as
	/// _VPOViewPosition.
	/// </summary>
	public void RenderViewWithShader(int viewIndex, Camera camera, Shader shader)
	{
		if (viewIndex < 0) { return; }

		replacementCommandBuffer ??= new CommandBuffer { name = "Indirect Replacement Shader View" };
		var commandBuffer = replacementCommandBuffer;
		commandBuffer.Clear();
		commandBuffer.SetRenderTarget(camera.targetTexture
			? new RenderTargetIdentifier(camera.targetTexture)
			: new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget));
		commandBuffer.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
		commandBuffer.SetGlobalVector(ViewPositionId, camera.transform.position);
		RecordViewDraws(viewIndex, commandBuffer, shader, camera.cullingMask);
		Graphics.ExecuteCommandBuffer(commandBuffer);
	}

	// Records a culled view's draws into commandBuffer with pass 0 of shader,
	// on copies of each batch's material, for batches on cullingMask's layers.
	private void RecordViewDraws(int viewIndex, CommandBuffer commandBuffer, Shader shader, int cullingMask)
	{
		for (var batchIndex = 0; batchIndex < runtimeBatches.Count; batchIndex += 1)
		{
			var runtimeBatch = runtimeBatches[batchIndex];
			if (runtimeBatch.RegisteredInstanceCount == 0 || !IsBatchVisible(viewIndex, batchIndex))
			{
				continue;
			}

			var renderParams = runtimeBatch.CameraRenderParams;
			if ((cullingMask & (1 << renderParams.layer)) == 0)
			{
				continue;
			}

			commandBuffer.DrawMeshInstancedIndirect(
				runtimeBatch.Mesh,
				runtimeBatch.SubMeshIndex,
				GetReplacementMaterial(renderParams.material, shader),
				0,
				indirectArgumentsBuffer,
				(viewIndex * runtimeBatches.Count + batchIndex) * GraphicsBuffer.IndirectDrawIndexedArgs.size,
				GetViewMaterialProperties(runtimeBatch, viewIndex));
		}
	}

	private MaterialPropertyBlock GetViewMaterialProperties(RuntimeBatch runtimeBatch, int viewIndex)
	{
		var viewProperties = runtimeBatch.ViewProperties;
		while (viewProperties.Count <= viewIndex)
		{
			viewProperties.Add(new BoundProperties());
		}

		return BindInstanceBuffers(
			viewProperties[viewIndex],
			visibleOffsetsBuffer,
			runtimeBatch.Index,
			viewIndex * visibleStride);
	}

	private Material GetReplacementMaterial(Material source, Shader shader)
	{
		if (replacementBySourceAndShader.TryGetValue((source, shader), out var replacement) && replacement)
		{
			return replacement;
		}

		replacement = new Material(shader)
		{
			name = $"{source.name} ({shader.name})", hideFlags = HideFlags.HideAndDontSave, enableInstancing = true,
		};
		replacement.CopyMatchingPropertiesFromMaterial(source);
		foreach (var keyword in source.shaderKeywords)
		{
			replacement.EnableKeyword(keyword);
		}

		replacementBySourceAndShader[(source, shader)] = replacement;
		return replacement;
	}

	private void CollectViews(IReadOnlyList<Light> shadowedLightCandidates)
	{
		views.Clear();
		(queuedViews, runningViews) = (runningViews, queuedViews);
		viewsCollected = true;
		// The light views come first, so their indices, which their shadow
		// command buffers record, do not move with the queued views.
		CollectShadowedLights(shadowedLightCandidates);
		queuedViewStart = FixedViewCount + views.Count;
		foreach (var queuedView in runningViews)
		{
			views.Add(queuedView.View);
		}

		CalculateViewFrustumPlanes();
		var requiredCapacity = FixedViewCount + views.Count;
		if (requiredCapacity > viewCapacity)
		{
			viewCapacity = FixedViewCount + Mathf.NextPowerOfTwo(views.Count);
			layoutDirty = true;
		}
	}

	private void CalculateViewFrustumPlanes()
	{
		if (viewFrustumPlanes.Length < views.Count * ViewPlaneCount)
		{
			viewFrustumPlanes = new Vector4[Mathf.NextPowerOfTwo(views.Count) * ViewPlaneCount];
		}

		for (var index = 0; index < views.Count; index += 1)
		{
			var view = views[index];
			var start = index * ViewPlaneCount;
			if (!view.HasFrustum)
			{
				for (var plane = 0; plane < ViewPlaneCount; plane += 1)
				{
					viewFrustumPlanes[start + plane] = new Vector4(0f, 0f, 0f, 1f);
				}

				continue;
			}

			GeometryUtility.CalculateFrustumPlanes(view.CullingMatrix, viewPlanes);
			for (var plane = 0; plane < ViewPlaneCount; plane += 1)
			{
				viewFrustumPlanes[start + plane] = viewPlanes[plane].ToVector4();
			}
		}
	}

	/// <summary>
	/// Culls the camera (view 0) and the collected views in one pass, or with
	/// viewsOnly, the collected views alone.
	/// </summary>
	private void DispatchViewCull(Matrix4x4 viewProjectionMatrix, int instanceCount, bool viewsOnly = false)
	{
		var cameraViewCount = viewsOnly ? 0 : 1;
		var viewCount = cameraViewCount + views.Count;
		var batchCount = runtimeBatches.Count;
		// The cascades' draws are reset too; the shadow pass resets them again
		// before culling them.
		var clearStart = viewsOnly ? FixedViewCount : 0;
		ClearInstanceCounts(
			indirectArgumentsBuffer,
			clearStart * batchCount,
			(FixedViewCount + views.Count - clearStart) * batchCount);

		if (viewDataBuffer == null || viewDataBuffer.count < viewCount)
		{
			viewDataBuffer?.Release();
			viewDataBuffer = new GraphicsBuffer(
				GraphicsBuffer.Target.Structured,
				Mathf.NextPowerOfTwo(viewCount),
				Marshal.SizeOf<ViewData>());
			viewData = new ViewData[viewDataBuffer.count];
		}

		if (!viewsOnly)
		{
			var cameraView = new ViewData { LodParameters = cameraLodParameters, ViewIndex = 0, Flags = ViewScreenSizeFlag };
			SetPlanes(ref cameraView, cameraFrustumPlanes);
			viewData[0] = cameraView;
		}

		for (var index = 0; index < views.Count; index += 1)
		{
			var view = views[index];
			var data = new ViewData
			{
				LodParameters = new Vector4(view.LodOrigin.x, view.LodOrigin.y, view.LodOrigin.z, view.LodScale),
				Sphere = view.Sphere, ViewIndex = (uint)(FixedViewCount + index), Flags
					= (view.ShadowCastersOnly ? ViewShadowCastersOnlyFlag : 0u)
					| (view.CubeFaces ? ViewCubeFacesFlag : 0u),
			};
			var start = index * ViewPlaneCount;
			data.Plane0 = viewFrustumPlanes[start];
			data.Plane1 = viewFrustumPlanes[start + 1];
			data.Plane2 = viewFrustumPlanes[start + 2];
			data.Plane3 = viewFrustumPlanes[start + 3];
			data.Plane4 = viewFrustumPlanes[start + 4];
			data.Plane5 = viewFrustumPlanes[start + 5];
			viewData[cameraViewCount + index] = data;
		}

		viewDataBuffer.SetData(viewData, 0, 0, viewCount);

		var computeShader = shaders.FrustumCullShader;
		var kernel = shaders.ViewCullKernel;
		computeShader.SetMatrix(ViewProjectionMatrixId, viewProjectionMatrix);
		computeShader.SetFloat(MinimumScreenPercentageId, minimumScreenFraction);
		computeShader.SetInt(InstanceCountId, instanceCount);
		computeShader.SetInt(BatchCountId, batchCount);
		computeShader.SetInt(ViewCountId, viewCount);
		computeShader.SetInt(VisibleStrideId, visibleStride);
		computeShader.SetBuffer(kernel, ShadowInputInstancesId, instanceBuffer);
		computeShader.SetBuffer(kernel, BatchDataId, batchDataBuffer);
		computeShader.SetBuffer(kernel, CullVisibleInstanceIndicesId, visibleInstanceIndicesBuffer);
		computeShader.SetBuffer(kernel, IndirectArgumentsId, indirectArgumentsBuffer);
		computeShader.SetBuffer(kernel, ViewsId, viewDataBuffer);
		computeShader.SetBuffer(kernel, CullDiagnosticsId, cullDiagnosticsBuffer);
		// Render resets and reads back the diagnostics around its own cull only.
		computeShader.SetInt(CullDiagnosticsEnabledId, viewsOnly ? 0 : CullDiagnosticsEnabled);
		ComputeDispatch.GetDimensions(
			ComputeDispatch.GetThreadGroupCount(instanceCount, shaders.ViewCullGroupSize),
			out var dispatchX, out var dispatchY);
		computeShader.Dispatch(kernel, dispatchX, dispatchY, 1);
	}

	private static void SetPlanes(ref ViewData data, Plane[] planes)
	{
		data.Plane0 = planes[0].ToVector4();
		data.Plane1 = planes[1].ToVector4();
		data.Plane2 = planes[2].ToVector4();
		data.Plane3 = planes[3].ToVector4();
		data.Plane4 = planes[4].ToVector4();
		data.Plane5 = planes[5].ToVector4();
	}

	private void RunQueuedViews(bool culled)
	{
		if (!viewsCollected)
		{
			(queuedViews, runningViews) = (runningViews, queuedViews);
		}

		viewsCollected = false;

		for (var index = 0; index < runningViews.Count; index += 1)
		{
			var render = runningViews[index].Render;
			try
			{
				render(culled ? queuedViewStart + index : -1);
			}
			catch (Exception exception)
			{
				Debug.LogException(exception);
			}
		}

		runningViews.Clear();
	}
}
