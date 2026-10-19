using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using IndirectRendering.Collections;
using IndirectRendering.Extensions;

namespace IndirectRendering;

/// <summary>
/// Draws registered instances with GPU-culled indirect draws, for the camera,
/// the directional shadow cascades, shadowed local lights and queued views.
/// The host calls Render once a frame, after the camera and lights have moved
/// and before they render, and Dispose when it is done with it.
/// </summary>
public sealed partial class IndirectRenderer : IDisposable
{
	private const int MaxDirectionalCascades = 4;
	// View 0 is the camera; views 1-4 are the directional shadow cascades. The
	// views queued or tracked this frame follow, see CollectViews.
	private const int FixedViewCount = 1 + MaxDirectionalCascades;

	private static readonly int BatchDataId = Shader.PropertyToID("_BatchData");
	private static readonly int BatchCountId = Shader.PropertyToID("_BatchCount");
	private static readonly int CullVisibleInstanceIndicesId = Shader.PropertyToID("_VisibleInstanceIndices");
	private static readonly int IndirectArgumentsId = Shader.PropertyToID("_IndirectArguments");
	private static readonly int InstanceCountId = Shader.PropertyToID("_InstanceCount");
	private static readonly int ShadowInputInstancesId = Shader.PropertyToID("_InputInstances");
	private static readonly int ShadowReceiverParametersId = Shader.PropertyToID("_ShadowReceiverParameters");
	private static readonly int ShadowReceiverMapId = Shader.PropertyToID("_ShadowReceiverMap");
	private static readonly int ShadowCascadeSpheresId = Shader.PropertyToID("_ShadowCascadeSpheres");
	private static readonly int CullDiagnosticsId = Shader.PropertyToID("_CullDiagnostics");
	private static readonly int CullDiagnosticsEnabledId = Shader.PropertyToID("_CullDiagnosticsEnabled");
	private static readonly int IndirectArgumentWordCount = GraphicsBuffer.IndirectDrawIndexedArgs.size / sizeof(uint);

	// Indexed by batch id; see BatchDefinitions.
	private readonly List<RuntimeBatch> runtimeBatches = new();

	private readonly IndirectRendererShaders shaders;
	private readonly InstanceOverrideStore overrideStore;
	private float minimumScreenFraction;

	// Bumped when a buffer the draws' property blocks bind is replaced, or the
	// batch count moves their draw indices; see BindInstanceBuffers.
	private int materialBindingsVersion;
	// Bumped when anything the recorded shadow command buffers capture changes,
	// which includes every material bindings change.
	private int shadowRecordingVersion;

	/// <param name="overrideStore">
	/// Holds the game's per-instance override struct, which its shaders read as
	/// _VPOInstanceOverrides.
	/// </param>
	/// <param name="settings">Null for the defaults.</param>
	public IndirectRenderer(InstanceOverrideStore overrideStore, IndirectRendererSettings settings = null)
	{
		settings ??= new IndirectRendererSettings();
		BurstLibrary.EnsureLoaded();
		MultiDraw.EnsureInitialized();
		shaders = settings.Shaders ?? IndirectRendererShaders.LoadEmbedded();
		this.overrideStore = overrideStore;
		minimumScreenFraction = settings.MinimumScreenFraction;
		multiDrawCasterResolver = settings.MultiDrawCasterResolver;
		batchCells = new BatchCellGrid(settings.BatchCellSize);
		ValidateShadowBufferStrides();
		Current = this;
	}

	public static IndirectRenderer Current { get; private set; }

	/// <summary>
	/// While false, Register returns invalid handles and Unregister and
	/// SetInstanceOverride do nothing. See CloseRegistrations.
	/// </summary>
	public bool AcceptsRegistrations { get; private set; } = true;

	/// <summary>
	/// Ignores registrations until the next ResetScene, for a host tearing its
	/// scene down: instances destroyed then would each unregister from a
	/// renderer that is about to drop them all.
	/// </summary>
	public void CloseRegistrations()
	{
		AcceptsRegistrations = false;
	}

	public bool Enabled { get; private set; } = true;

	/// <summary>
	/// While disabled, Render does nothing and queued views render at once
	/// with no culled view.
	/// </summary>
	public void SetEnabled(bool enabled)
	{
		if (Enabled == enabled) { return; }

		Enabled = enabled;
		if (enabled) { return; }

		DetachShadowCommandBuffers();
		layoutDirty = true;
		RunQueuedViews(false);
	}

	/// <summary>
	/// See IndirectRendererSettings.MinimumScreenFraction.
	/// </summary>
	public void SetMinimumScreenFraction(float fraction)
	{
		minimumScreenFraction = fraction;
	}

	/// <summary>
	/// Culls and draws this frame for camera. shadowedLights are the point and
	/// spot lights with shadows, whose shadow maps get the indirect casters. A
	/// host that does not render this frame calls SkipFrame instead.
	/// </summary>
	public void Render(Camera camera, Light directionalLight, IReadOnlyList<Light> shadowedLights)
	{
		renderedFrame = Time.frameCount;
		if (!Enabled || !camera || !directionalLight)
		{
			SkipFrame();
			return;
		}

		Profiler.BeginSample("CameraSetup");
		var gpuProjectionMatrix = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
		var viewProjectionMatrix = gpuProjectionMatrix * camera.worldToCameraMatrix;
		GeometryUtility.CalculateFrustumPlanes(camera, cameraFrustumPlanes);
		UpdateCameraLodParameters(camera);

		BuildShadowCullingPlanes(camera, directionalLight);
		CollectViews(shadowedLights);
		Profiler.EndSample();

		Profiler.BeginSample("InstanceUpload");
		ApplyMovedInstances();
		UploadInstanceChanges();
		Profiler.EndSample();
		ValidateBatchesPeriodically();

		Profiler.BeginSample("BatchLayout");
		EnsureBatchLayout();
		Profiler.EndSample();
		var instanceCount = instanceSlots.LiveCount != 0 ? instanceSlots.HighWaterMark : 0;
		var hasShadowWork = instanceCount != 0 && shadowCascadeCount != 0;
		// The batch cull leaves the multi-draw casters out of the shadow draws.
		SetMultiDrawActive(hasShadowWork && PrepareMultiDrawCasters());
		if (instanceCount != 0)
		{
			Profiler.BeginSample("BatchCullSchedule");
			ScheduleBatchCull();
			Profiler.EndSample();
		}

		if (hasShadowWork)
		{
			EnsureShadowFixedBuffers();
		}

		EnsureCullDiagnosticsBuffer();
		PrepareCullDiagnostics(instanceCount);
	#if DEBUG
		unityShadowSubmissionCount = 0;
		cameraSubmissionCount = 0;
	#endif
		UpdateShadowReceiverResources(camera, directionalLight, hasShadowWork);

		if (instanceCount != 0)
		{
			Profiler.BeginSample("ViewCulling");
			DispatchViewCull(viewProjectionMatrix, instanceCount);
			Profiler.EndSample();

			Profiler.BeginSample("BatchCullWait");
			CompleteBatchCull();
			Profiler.EndSample();

			SubmitCameraDraws(camera);
		}

		if (hasShadowWork)
		{
			Profiler.BeginSample("ShadowBufferUpload");
			UploadShadowBuffers(instanceCount);
			Profiler.EndSample();
		}

		UpdateShadowCommandBufferAttachments(directionalLight, hasShadowWork);
		if (hasShadowWork)
		{
			Profiler.BeginSample("ShadowPreparationRecording");
			RecordShadowPreparationCommandBuffers(directionalLight.cullingMask);
			Profiler.EndSample();
			SubmitDirectionalShadowPlaceholder(camera);
		}

		Profiler.BeginSample("LocalShadowRecording");
		RecordLocalShadowCommandBuffers(hasShadowWork);
		Profiler.EndSample();
		if (hasShadowWork)
		{
			SubmitLocalShadowPlaceholders(camera);
		}

		Profiler.BeginSample("QueuedViews");
		RunQueuedViews(instanceCount != 0);
		Profiler.EndSample();

		LogShadowDiagnostics();
	}

	/// <summary>
	/// Detaches the shadow command buffers and renders the queued views with
	/// no culled view.
	/// </summary>
	public void SkipFrame()
	{
		// Moves are drained every frame, so a disabled renderer resumes with
		// current transforms.
		ApplyMovedInstances();
		if (ScatterInstances())
		{
			InvalidateMaterialBindings();
		}

		if (!Enabled) { return; }

		DetachShadowCommandBuffers();
		RunQueuedViews(false);
	}

	private void InvalidateMaterialBindings()
	{
		materialBindingsVersion += 1;
		InvalidateShadowRecordings();
	}

	private void InvalidateShadowRecordings()
	{
		shadowRecordingVersion += 1;
	}

	private void UploadInstanceChanges()
	{
		var instanceBufferReplaced = ScatterInstances();
		var overrideBufferReplaced = overrideStore.TryUpload();
		if (instanceBufferReplaced || overrideBufferReplaced)
		{
			InvalidateMaterialBindings();
		}
	}

	private void DisposeGpuResources()
	{
		ReleaseBatchBuffers();
		ReleaseBuffer(ref visibleInstanceIndicesBuffer);
		ReleaseBuffer(ref shadowCullParametersBuffer);
		ReleaseBuffer(ref shadowCullingPlanesBuffer);
		ReleaseBuffer(ref viewDataBuffer);
		foreach (var replacement in replacementBySourceAndShader.Values)
		{
			if (replacement)
			{
				UnityEngine.Object.Destroy(replacement);
			}
		}
		replacementBySourceAndShader.Clear();
		ReleaseBuffer(ref shadowDispatchArgumentsBuffer);
		ReleaseBuffer(ref shadowReceiverParametersBuffer);
		ReleaseBuffer(ref shadowReceiverMapBuffer);
		ReleaseBuffer(ref shadowDepthTilesBuffer);
		ReleaseBuffer(ref shadowCascadeSpheresBuffer);
		ReleaseBuffer(ref emptyShadowArgumentsBuffer);
		ReleaseMultiDrawShadows();
		if (shadowCascadeCaptureMaterial)
		{
			UnityEngine.Object.Destroy(shadowCascadeCaptureMaterial);
		}
		shadowCascadeCaptureMaterial = null;
		ReleaseBuffer(ref cullDiagnosticsBuffer);

		batchData = Array.Empty<BatchData>();
		batchVisibleOffsets = Array.Empty<uint>();
		batchArguments = Array.Empty<uint>();
		batchCapacity = 0;
		visibleStride = 0;
		allocatedViewCapacity = 0;
		layoutBatchCount = 0;
		layoutDirty = true;
		InvalidateMaterialBindings();
		shadowReceiverTileCountX = 0;
		shadowReceiverTileCountY = 0;
		shadowReceiverShadowFar = 0f;
	#if DEBUG
		unityShadowSubmissionCount = 0;
	#endif
	}

	private static void ReleaseBuffer(ref GraphicsBuffer buffer)
	{
		buffer?.Release();
		buffer = null;
	}

	/// <summary>
	/// Drops every registered instance and override and the GPU resources.
	/// Batch definitions stay; see BatchDefinitions.
	/// </summary>
	public void ResetScene()
	{
		DetachShadowCommandBuffers();
		CompleteBatchCull();
		culledBatchCount = 0;
		DisposeGpuResources();
		runtimeBatches.Clear();
		batchCells.Clear();
		ResetInstances();
		overrideStore.Reset();
		SyncRuntimeBatches();
		AcceptsRegistrations = true;
	}

	private RuntimeBatch GetRuntimeBatch(int batchId)
	{
		if (batchId >= runtimeBatches.Count)
		{
			SyncRuntimeBatches();
		}

		return runtimeBatches[batchId];
	}

	private static IndirectInstanceData PackInstance(
		RuntimeBatch runtimeBatch,
		Matrix4x4 objectToWorld,
		Vector4 lodRange,
		int overrideIndex)
	{
		var worldToObject = default(Matrix4x4);
		Matrix4x4.Inverse3DAffine(objectToWorld, ref worldToObject);
		// The bottom rows of affine matrices are (0, 0, 0, 1); the shaders
		// restore them. worldToObject's carries the override index and the
		// batch index plus one, so zeroed free slots match no batch.
		worldToObject.m30 = overrideIndex;
		worldToObject.m31 = runtimeBatch.Index + 1;
		// objectToWorld's carries the LOD range; see BatchedInstance.GetLodRange.
		objectToWorld.SetRow(3, lodRange);
		return new IndirectInstanceData { ObjectToWorld = objectToWorld, WorldToObject = worldToObject };
	}

	private void AddToBatch(RuntimeBatch runtimeBatch, Matrix4x4 objectToWorld, Vector4 lodRange)
	{
		var bounds = runtimeBatch.MeshBounds.TransformBy(objectToWorld);
		float3 boundsMinimum = bounds.min;
		float3 boundsMaximum = bounds.max;
		batchCells.Add(runtimeBatch.Index, GetInstancePosition(objectToWorld), boundsMinimum, boundsMaximum, lodRange);
		if (runtimeBatch.RegisteredInstanceCount == 0)
		{
			runtimeBatch.WorldBoundsMinimum = boundsMinimum;
			runtimeBatch.WorldBoundsMaximum = boundsMaximum;
			runtimeBatch.CameraRenderParams.worldBounds = bounds;
		}
		else
		{
			GrowWorldBounds(runtimeBatch, boundsMinimum, boundsMaximum);
		}

		runtimeBatch.RegisteredInstanceCount += 1;
		if (runtimeBatch.RegisteredInstanceCount > runtimeBatch.VisibleCapacity)
		{
			layoutDirty = true;
		}
	}

	private static void GrowWorldBounds(RuntimeBatch runtimeBatch, float3 boundsMinimum, float3 boundsMaximum)
	{
		if (math.all((boundsMinimum >= runtimeBatch.WorldBoundsMinimum) & (boundsMaximum <= runtimeBatch.WorldBoundsMaximum)))
		{
			return;
		}

		runtimeBatch.WorldBoundsMinimum = math.min(runtimeBatch.WorldBoundsMinimum, boundsMinimum);
		runtimeBatch.WorldBoundsMaximum = math.max(runtimeBatch.WorldBoundsMaximum, boundsMaximum);
		runtimeBatch.CameraRenderParams.worldBounds = runtimeBatch.WorldBounds;
	}

	private void RemoveFromBatch(RuntimeBatch runtimeBatch, Vector3 position)
	{
		// An emptied batch keeps its visible range until the next relayout:
		// the visible stride never shrinks, so reclaiming it early saves nothing.
		batchCells.Remove(runtimeBatch.Index, position);
		runtimeBatch.RegisteredInstanceCount -= 1;
	}

	public int AllocateOverride()
	{
		return overrideStore.Allocate();
	}

	/// <summary>
	/// T must be the struct of the store from CreateOverrideStore.
	/// </summary>
	public void UpdateOverride<T>(int overrideIndex, in T data) where T : struct
	{
		((InstanceOverrideStore<T>)overrideStore).Update(overrideIndex, data);
	}

	public void ReleaseOverride(int overrideIndex)
	{
		overrideStore.Release(overrideIndex);
	}

	public void Dispose()
	{
		ResetScene();
		DisposeBatchCulling();
		DisposeShadowCommandBuffers();
		transformTracker.Dispose();
		overrideStore.Dispose();
		if (Current == this)
		{
			Current = null;
		}
	}
}
