using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

public class OcclusionRenderer : MonoBehaviour
{
	public static OcclusionRenderer Instance { get; private set; }
	
	public ComputeShader occlusionCS;
	public DownSampleDepth depthDownSampler;
	public Shader depthShader;
	public Camera depthCamera;

	private MeshRenderer[] allRenderers;

	private ComputeBuffer instanceDataBuffer;
	private ComputeBuffer visibilityBuffer;

	private NativeArray<GPUVisibilityData> visibleInstances;

	private int occlusionKernelId;

	private RenderTexture depthTexture;
	private RenderTexture hiZDepthTexture;

	private readonly SparseDictionary<GPUInstanceData> instanceData
		= new SparseDictionary<GPUInstanceData>(MAX_INSTANCES, MAX_INSTANCES);

	private readonly SparseDictionary<RendererTracker> trackedInstances
		= new SparseDictionary<RendererTracker>(MAX_INSTANCES, MAX_INSTANCES);

	private int nextSerialId;
	private int dirtyIndexStart;

	private bool instanceDataDirty;

	private readonly int[] lastVisibilityState = new int[MAX_INSTANCES];

	public static readonly Stack<int> FreeIDs = new Stack<int>();

	private static readonly int MAX_INSTANCES = (int)Mathf.Pow(2, 20);

	private static readonly int MvpMatrixId = Shader.PropertyToID("_UNITY_MATRIX_MVP");
	private static readonly int CamPositionId = Shader.PropertyToID("_CamPosition");
	private static readonly int HiZTextureSize = Shader.PropertyToID("_HiZTextureSize");
	private static readonly int CountId = Shader.PropertyToID("_Count");
	private static readonly int HiZTextureId = Shader.PropertyToID("_HiZMap");
	private static readonly int CullingPlanesId = Shader.PropertyToID("_CullingPlanes");
	private static readonly int ShouldOcclusionCullId = Shader.PropertyToID("_ShouldOcclusionCull");
	private static readonly int ShouldCullInvisibleId = Shader.PropertyToID("_ShouldCullInvisible");

	private static readonly int SizeOfVisibilityData = Marshal.SizeOf(typeof(GPUVisibilityData));

	[StructLayout(LayoutKind.Sequential)]
	public struct GPUInstanceData
	{
		public Vector3 boundsCenter;  // 3
		public Vector3 boundsExtents; // 6
		public uint instanceId;		  // 1
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct GPUVisibilityData
	{
		public uint isVisible;  // 1
		public uint instanceId; // 1
	}

	private void Awake()
	{
		if (Instance == null)
		{
			Instance = this;
		}
		else if (Instance != this)
		{
			Destroy(this);
		}

		allRenderers = Resources.FindObjectsOfTypeAll<MeshRenderer>();
		foreach (var meshRenderer in allRenderers)
		{
			meshRenderer.gameObject.AddComponent<RendererTracker>();
		}
	}

	private void OnEnable()
	{
		visibleInstances = new NativeArray<GPUVisibilityData>(MAX_INSTANCES, Allocator.Persistent);
		
		var instanceDataSize = Marshal.SizeOf(typeof(GPUInstanceData));
		instanceDataBuffer = new ComputeBuffer(MAX_INSTANCES, instanceDataSize, ComputeBufferType.Default);
		visibilityBuffer = new ComputeBuffer(MAX_INSTANCES, SizeOfVisibilityData, ComputeBufferType.Default);

		occlusionKernelId = occlusionCS.FindKernel("CSMain");

		instanceDataBuffer.SetData(instanceData.Values);
		
		// all 0s
		visibilityBuffer.SetData(visibleInstances);

		occlusionCS.SetBuffer(occlusionKernelId, Shader.PropertyToID("_InstanceDataBuffer"), instanceDataBuffer);
		occlusionCS.SetBuffer(occlusionKernelId, Shader.PropertyToID("_VisibilityBuffer"), visibilityBuffer);

		for (var i = 0; i < instanceData.Count; i++)
		{
			var shadowCastingMode = trackedInstances.Values[i].Renderer.shadowCastingMode;

			lastVisibilityState[i] = shadowCastingMode == ShadowCastingMode.On ? 1 : 0;
		}
	}

	public int AddInstance(RendererTracker tracker)
	{
		var rendererBounds = tracker.Renderer.bounds;
		
		var instanceId = GetFreeID();
		var indirectInstanceData = new GPUInstanceData
		{
			boundsCenter = rendererBounds.center, boundsExtents = rendererBounds.extents,
			instanceId = (uint)instanceId,
		};

		var dirtyIndex = instanceData.Add(instanceId, indirectInstanceData);
		trackedInstances.Add(instanceId, tracker);

		dirtyIndexStart = Math.Min(dirtyIndexStart, dirtyIndex);
		instanceDataDirty = true;

		lastVisibilityState[dirtyIndex] = tracker.Renderer.shadowCastingMode == ShadowCastingMode.On ? 1 : 0;
		
		return instanceId;
	}

	public void RemoveInstance(RendererTracker tracker)
	{
		var count = instanceData.Count;
		var dirtyIndex = instanceData.Remove(tracker.ID);
		trackedInstances.Remove(tracker.ID);

		dirtyIndexStart = Math.Min(dirtyIndexStart, dirtyIndex);
		instanceDataDirty = true;
		
		var lastVisible = lastVisibilityState[count - 1];
		lastVisibilityState[dirtyIndex] = lastVisible;
		lastVisibilityState[count - 1] = 0;

		FreeIDs.Push(tracker.ID);
	}

	private void OnDisable()
	{
		visibilityBuffer?.Release();
		instanceDataBuffer?.Release();

		visibleInstances.Dispose();
	}

	private void LateUpdate()
	{
		if (instanceDataDirty)
		{
			instanceDataBuffer.SetData(
				instanceData.Values,
				dirtyIndexStart, 
				dirtyIndexStart,
				instanceData.Count - dirtyIndexStart);
			instanceDataDirty = false;
		}

		CalculateVisibleInstances(Camera.main);
		RenderInstances();
	}

	private void CalculateVisibleInstances(Camera cam)
	{
		if (cam == null) { return; }

		RecreateTextures(cam);

		Graphics.SetRenderTarget(hiZDepthTexture);
		GL.Clear(true, true, Color.clear);

		var camPosition = cam.transform.position;
		Plane[] planes = GeometryUtility.CalculateFrustumPlanes(cam);
		Vector4[] encodedPlanes = planes
			.Select(plane => new Vector4(plane.normal.x, plane.normal.y, plane.normal.z, plane.distance))
			.ToArray();

		var v = cam.worldToCameraMatrix;
		var p = cam.projectionMatrix;
		var mvp = p * v;
		
		var instanceCount = instanceData.Count;

		// Common setup
		occlusionCS.SetMatrix(MvpMatrixId, mvp);
		occlusionCS.SetVector(CamPositionId, camPosition);
		occlusionCS.SetVectorArray(CullingPlanesId, encodedPlanes);
		occlusionCS.SetInt(CountId, instanceCount);

		var dispatchSize = Mathf.Max(1, Mathf.NextPowerOfTwo(instanceCount) / 32);

		var asyncCallback = CreateInstanceVisibleCallback(instanceCount);
		Profiler.BeginSample("Pass 1");
		{
			occlusionCS.SetInt(ShouldOcclusionCullId, 0);
			occlusionCS.SetInt(ShouldCullInvisibleId, 0);

			occlusionCS.Dispatch(occlusionKernelId, dispatchSize, 1, 1);

			AsyncGPUReadback.Request(visibilityBuffer, instanceCount * SizeOfVisibilityData, 0, asyncCallback);
			
		}
		Profiler.EndSample();

		/*
		 * For some retarded ass reason the camera will always render every pass
		 * even if you disable it and set DepthTextureMode.Depth
		 * I figured out that a combination of setting the renderpath to forward
		 * and rendering with a replacement shader does indeed only render the shit I want,
		 * but it still tries to clear and collect shadows even if there are no shadow draws
		 */

		Profiler.BeginSample("Draw Pass 1 results");
		cam.renderingPath = RenderingPath.Forward;
		cam.targetTexture = depthTexture;
		cam.forceIntoRenderTexture = true;
		cam.RenderWithShader(depthShader, "RenderType");
		depthDownSampler.DownSample(depthTexture, hiZDepthTexture);

		cam.forceIntoRenderTexture = false;
		cam.targetTexture = null;
		cam.renderingPath = RenderingPath.DeferredShading;
		Profiler.EndSample();

		Profiler.BeginSample("Downsample");
		Profiler.EndSample();

		Profiler.BeginSample("Pass 2");
		{
			occlusionCS.SetInt(ShouldOcclusionCullId, 1);
			occlusionCS.SetInt(ShouldCullInvisibleId, 1);

			occlusionCS.Dispatch(occlusionKernelId, dispatchSize, 1, 1);
			AsyncGPUReadback.Request(visibilityBuffer, instanceCount * SizeOfVisibilityData, 0, asyncCallback);
		}
		Profiler.EndSample();
	}

	private void RenderInstances()
	{

	}

	private Action<AsyncGPUReadbackRequest> CreateInstanceVisibleCallback(int instanceCount)
	{
		return (req) => OnVisibleInstanceAsyncReadback(req, instanceCount);
	}
	
	private void OnVisibleInstanceAsyncReadback(AsyncGPUReadbackRequest req, int instanceCount)
	{
		var visible = req.GetData<GPUVisibilityData>();
		
		Profiler.BeginSample("xdd");
		for (var i = 0; i < instanceCount; i++)
		{
			Profiler.BeginSample("get from nativearray");
			var visibilityData = visible[i];
			var isVisible = visibilityData.isVisible;
			
			// setting shadowcasting mode is insanely slow so I packed a "wasVisible" bit at index 1
			// so we can skip this entire operation
			if (isVisible == lastVisibilityState[i])
			{
				continue;
			}

			lastVisibilityState[i] = (int)isVisible;
			
			Profiler.EndSample();

			Profiler.BeginSample("getvalue");
			var found = trackedInstances.TryGetValue((int)visibilityData.instanceId, out var tracker);
			Profiler.EndSample();
			if (found)
			{
				Profiler.BeginSample("set value");
				tracker.Renderer.shadowCastingMode
					= visibilityData.isVisible == 0 ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
				Profiler.EndSample();
			}
		}
		Profiler.EndSample();
	}

	private void RecreateTextures(Camera cam)
	{
		if (depthTexture == null || cam.pixelWidth != depthTexture.width || cam.pixelHeight != depthTexture.height)
		{
			if (depthTexture != null)
			{
				depthTexture.Release();
			}

			depthTexture = new RenderTexture(cam.pixelWidth, cam.pixelHeight, 32, RenderTextureFormat.Depth);
			depthTexture.Create();
		}

		var size = Mathf.Max(cam.pixelWidth, cam.pixelHeight);
		size = Mathf.Min(Mathf.NextPowerOfTwo(size), 2048);
		if (hiZDepthTexture == null || hiZDepthTexture.width != size || hiZDepthTexture.height != size)
		{
			if (hiZDepthTexture != null)
			{
				hiZDepthTexture.Release();
			}

			hiZDepthTexture
				= new RenderTexture(size, size, 0, RenderTextureFormat.RHalf, RenderTextureReadWrite.Linear);
			hiZDepthTexture.filterMode = FilterMode.Point;
			hiZDepthTexture.useMipMap = true;
			hiZDepthTexture.autoGenerateMips = false;
			hiZDepthTexture.Create();
			hiZDepthTexture.hideFlags = HideFlags.HideAndDontSave;

			occlusionCS.SetVector(HiZTextureSize, new Vector4(size, size));
			occlusionCS.SetTexture(occlusionKernelId, HiZTextureId, hiZDepthTexture);
		}
	}

	private int GetFreeID()
	{
		if (FreeIDs.Count > 0)
		{
			return FreeIDs.Pop();
		}

		var instanceId = nextSerialId;
		nextSerialId += 1;
		if (nextSerialId >= MAX_INSTANCES)
		{
			nextSerialId = 0;
		}

		return instanceId;
	}
}
