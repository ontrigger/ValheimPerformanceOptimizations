using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;
using UnityEngine.PostProcessing;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace ValheimPerformanceOptimizations.Patches.OcclusionCulling
{
	[HarmonyPatch]
	public partial class VPOOcclusionRenderer : MonoBehaviour
	{
		public static VPOOcclusionRenderer Instance { get; private set; }

		private ComputeBuffer instanceDataBuffer;
		private ComputeBuffer visibilityBuffer;

		private NativeArray<GPUVisibilityData> visibleInstances;

		private int occlusionKernelId;

		private RenderTexture depthTexture;
		private RenderTexture hiZDepthTexture;

		private readonly SparseDictionary<GPUInstanceData> instanceData = new(MAX_INSTANCES, MAX_INSTANCES);

		private readonly SparseDictionary<VPORendererTracker> trackedInstances = new(MAX_INSTANCES, MAX_INSTANCES);

		private int nextSerialId = 1;
		private int dirtyIndexStart;

		private bool instanceDataDirty;
		
		private readonly int[] lastVisibilityState = new int[MAX_INSTANCES];

		private static ComputeShader _occlusionCs;
		private static Shader _depthShader;
		private static Material _downsampleMaterial;

		private readonly Stack<int> freeIDs = new(MAX_INSTANCES);

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

		private const string AssetBundleName = "occlusion_shaders";
		private const string OcclusionShaderAssetPath = "Assets/Shaders/OcclusionCS.compute";
		private const string DownsampleShaderAssetPath = "Assets/Shaders/DownsampleDepth.shader";
		private const string DepthOnlyShaderAssetPath = "Assets/Shaders/DepthOnly.shader";

		[StructLayout(LayoutKind.Sequential)]
		public struct GPUInstanceData
		{
			public Vector3 boundsCenter;  // 3
			public Vector3 boundsExtents; // 6
			public uint instanceId;       // 1
		}

		[StructLayout(LayoutKind.Sequential)]
		public struct GPUVisibilityData
		{
			public uint isVisible;  // 1
			public uint instanceId; // 1
		}

		[HarmonyPostfix, HarmonyPatch(typeof(Game), nameof(Game.Awake))]
		public static void Game_Awake_Postfix(Game __instance)
		{
			var assetBundle = AssetBundleHelper.GetAssetBundleFromResources(AssetBundleName);

			_occlusionCs = assetBundle.LoadAsset<ComputeShader>(OcclusionShaderAssetPath);
			_downsampleMaterial = new Material(assetBundle.LoadAsset<Shader>(DownsampleShaderAssetPath));
			_depthShader = assetBundle.LoadAsset<Shader>(DepthOnlyShaderAssetPath);
		}

		[HarmonyPostfix, HarmonyPatch(typeof(Game), nameof(Game.Update))]
		public static void Game_Update_Postfix(Game __instance)
		{
			if (Input.GetKeyUp("o"))
			{
				Instance.enabled = !Instance.enabled;
			}
		}

		[HarmonyPostfix, HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
		public static void ZNetScene_Awake_Postfix(ZNetScene __instance)
		{
			__instance.gameObject.AddComponent<VPOOcclusionRenderer>();
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

			foreach (var prefab in ZNetScene.instance.m_namedPrefabs.Values)
			{
				prefab
					.GetComponentsInChildren<MeshRenderer>(true)
					.Where(renderer => renderer.sharedMaterials
						.Where(mat => mat != null)
						.All(IsValidMaterial)
					)
					.Do(renderer =>
					{
						var tracker = renderer.gameObject.AddComponent<VPORendererTracker>();
						tracker.renderer = renderer;
					});
			}
		}

		private static bool IsValidMaterial(Material mat)
		{
			return true;
		}

		private void OnEnable()
		{
			visibleInstances = new NativeArray<GPUVisibilityData>(MAX_INSTANCES, Allocator.Persistent);

			var instanceDataSize = Marshal.SizeOf(typeof(GPUInstanceData));
			instanceDataBuffer = new ComputeBuffer(MAX_INSTANCES, instanceDataSize, ComputeBufferType.Default);
			visibilityBuffer = new ComputeBuffer(MAX_INSTANCES, SizeOfVisibilityData, ComputeBufferType.Default);

			occlusionKernelId = _occlusionCs.FindKernel("CSMain");

			Profiler.BeginSample("set instance data");
			instanceDataBuffer.SetData(instanceData.Values);
			Profiler.EndSample();

			Profiler.BeginSample("set vis data");
			// all 0s
			visibilityBuffer.SetData(visibleInstances);
			Profiler.EndSample();

			_occlusionCs.SetBuffer(occlusionKernelId, Shader.PropertyToID("_InstanceDataBuffer"), instanceDataBuffer);
			_occlusionCs.SetBuffer(occlusionKernelId, Shader.PropertyToID("_VisibilityBuffer"), visibilityBuffer);
			
			for (var i = 0; i < instanceData.Count; i++)
			{
				var shadowCastingMode = trackedInstances.Values[i].renderer.shadowCastingMode;

				lastVisibilityState[i] = shadowCastingMode == ShadowCastingMode.On ? 1 : 0;
			}
		}

		public int AddInstance(VPORendererTracker tracker)
		{
			var rendererBounds = tracker.renderer.bounds;

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
			
			lastVisibilityState[dirtyIndex] = tracker.renderer.shadowCastingMode == ShadowCastingMode.On ? 1 : 0;

			return instanceId;
		}

		public void RemoveInstance(VPORendererTracker tracker)
		{
			Profiler.BeginSample("remove from tracked");
			var count = instanceData.Count;
			var dirtyIndex = instanceData.Remove(tracker.ID);
			trackedInstances.Remove(tracker.ID);
			Profiler.EndSample();

			dirtyIndexStart = Math.Min(dirtyIndexStart, dirtyIndex);
			instanceDataDirty = true;
			
			Profiler.BeginSample("swap lastvisivle");
			var lastVisible = lastVisibilityState[count - 1];
			lastVisibilityState[dirtyIndex] = lastVisible;
			lastVisibilityState[count - 1] = 0;
			Profiler.EndSample();

			Profiler.BeginSample("freeids push");
			freeIDs.Push(tracker.ID);
			Profiler.EndSample();
		}

		private void OnDisable()
		{
			visibilityBuffer?.Release();
			instanceDataBuffer?.Release();

			if (visibleInstances.IsCreated)
			{
				visibleInstances.Dispose();
			}
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
			if (cam == null || instanceData.Count == 0) { return; }

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

			// Common setup
			_occlusionCs.SetMatrix(MvpMatrixId, mvp);
			_occlusionCs.SetVector(CamPositionId, camPosition);
			_occlusionCs.SetVectorArray(CullingPlanesId, encodedPlanes);
			_occlusionCs.SetInt(CountId, instanceData.Count);

			var dispatchSize = Mathf.Max(1, Mathf.NextPowerOfTwo(instanceData.Count) / 32);

			Action<AsyncGPUReadbackRequest> asyncCallback = CreateInstanceVisibleCallback(instanceData.Count);
			Profiler.BeginSample("Pass 1");
			{
				_occlusionCs.SetInt(ShouldOcclusionCullId, 0);
				_occlusionCs.SetInt(ShouldCullInvisibleId, 0);

				_occlusionCs.Dispatch(occlusionKernelId, dispatchSize, 1, 1);

				AsyncGPUReadback.Request(visibilityBuffer, instanceData.Count * SizeOfVisibilityData, 0, asyncCallback);

			}
			Profiler.EndSample();

			/*
			 * For some retarded ass reason the camera will always render every pass
			 * even if you disable it and set DepthTextureMode.Depth
			 * I figured out that a combination of setting the renderpath to forward
			 * and rendering with a replacement shader does indeed only render the shit I want,
			 * but it still tries to clear and collect shadows even if there are no shadow draws
			 */

			// TODO: get rid of this and render everything manually
			Profiler.BeginSample("Draw Pass 1 results");
			if (cam.TryGetComponent<PostProcessingBehaviour>(out var pp))
			{
				pp.enabled = false;
			}
			
			Shader.EnableKeyword("INSTANCING_ON");
			cam.renderingPath = RenderingPath.Forward;
			cam.targetTexture = depthTexture;
			cam.RenderWithShader(_depthShader, "RenderType");
			cam.targetTexture = null;
			cam.renderingPath = RenderingPath.DeferredShading;
			if (pp != null)
			{
				pp.enabled = true;
			}
			Profiler.EndSample();

			Profiler.BeginSample("Downsample");
			DownsampleDepth(depthTexture, hiZDepthTexture);
			Profiler.EndSample();

			Profiler.BeginSample("Pass 2");
			{
				_occlusionCs.SetInt(ShouldOcclusionCullId, 1);
				_occlusionCs.SetInt(ShouldCullInvisibleId, 1);

				_occlusionCs.Dispatch(occlusionKernelId, dispatchSize, 1, 1);
				AsyncGPUReadback.Request(visibilityBuffer, instanceData.Count * SizeOfVisibilityData, 0, asyncCallback);
			}
			Profiler.EndSample();
		}

		private void RenderInstances()
		{

		}

		private Action<AsyncGPUReadbackRequest> CreateInstanceVisibleCallback(int instanceCount)
		{
			return req => OnVisibleInstanceAsyncReadback(req, instanceCount);
		}

		private void OnVisibleInstanceAsyncReadback(AsyncGPUReadbackRequest req, int instanceCount)
		{
			if (!req.done || req.hasError)
			{
				Debug.LogError("Why isnt it done");
				return;
			}
			
			NativeArray<GPUVisibilityData> visible = req.GetData<GPUVisibilityData>();

			Profiler.BeginSample("xdd");
			for (var i = 0; i < instanceCount; i++)
			{
				Profiler.BeginSample("get from nativearray");
				var visibilityData = visible[i];
				var isVisible = visibilityData.isVisible;
				Profiler.EndSample();

				// setting shadowcasting mode is insanely slow so I packed a "wasVisible" bit at index 1
				// so we can skip this entire operation
				if (isVisible == lastVisibilityState[i])
				{
					continue;
				}

				Profiler.BeginSample("getvalue");
				var found = trackedInstances.TryGetValue((int)visibilityData.instanceId, out var tracker);
				Profiler.EndSample();
				if (found)
				{
					Profiler.BeginSample("set value");
					tracker.renderer.shadowCastingMode
						= visibilityData.isVisible == 0 ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
					lastVisibilityState[i] = (int)isVisible;
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
				

				depthTexture = new RenderTexture(cam.pixelWidth, cam.pixelHeight, 24, RenderTextureFormat.Depth);
				depthTexture.Create();
				Debug.Log("Recreating src depth " + depthTexture.depth);
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

				_occlusionCs.SetVector(HiZTextureSize, new Vector4(size, size));
				_occlusionCs.SetTexture(occlusionKernelId, HiZTextureId, hiZDepthTexture);
				
				Debug.Log("Recreating dst depth " + hiZDepthTexture.depth);
			}
		}

		private int GetFreeID()
		{
			if (freeIDs.Count > 0)
			{
				return freeIDs.Pop();
			}

			var instanceId = nextSerialId;
			nextSerialId += 1;
			if (nextSerialId >= MAX_INSTANCES)
			{
				nextSerialId = 1;
			}

			return instanceId;
		}
	}
}
