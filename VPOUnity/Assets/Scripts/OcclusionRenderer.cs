using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
public class OcclusionRenderer : MonoBehaviour
{
	public ComputeShader occlusionCS;
	public DownSampleDepth depthDownSampler;
	public Shader depthShader;
	public Camera depthCamera;
	
	private MeshRenderer[] allRenderers;

	private ComputeBuffer instanceDataBuffer;
	private ComputeBuffer visibilityBuffer;

	private uint[] visibleInstances;

	private int occlusionKernelId;

	private RenderTexture depthTexture;
	private RenderTexture hiZDepthTexture;

	private List<IndirectInstanceData> instanceData = new List<IndirectInstanceData>();

	private static readonly int MvpMatrixId = Shader.PropertyToID("_UNITY_MATRIX_MVP");
	private static readonly int CamPositionId = Shader.PropertyToID("_CamPosition");
	private static readonly int HiZTextureSize = Shader.PropertyToID("_HiZTextureSize");
	private static readonly int CountId = Shader.PropertyToID("_Count");
	private static readonly int HiZTextureId = Shader.PropertyToID("_HiZMap");
	private static readonly int ShouldOcclusionCullId = Shader.PropertyToID("_ShouldOcclusionCull");
	private static readonly int ShouldCullInvisibleId = Shader.PropertyToID("_ShouldCullInvisible");

	[StructLayout(LayoutKind.Sequential)]
	public struct IndirectInstanceData
	{
		public Vector3 boundsCenter;  // 3
		public Vector3 boundsExtents; // 6
	}
	
	[StructLayout(LayoutKind.Sequential)]
	public struct Indirect2x2Matrix
	{
		public Vector4 row0; // 4
		public Vector4 row1; // 8
	};
	
	private void Awake()
	{
		allRenderers = Resources.FindObjectsOfTypeAll<MeshRenderer>();
		visibleInstances = new uint[allRenderers.Length];
		foreach (var meshRenderer in allRenderers)
		{
			instanceData.Add(new IndirectInstanceData
			{
				boundsCenter = meshRenderer.bounds.center,
				boundsExtents = meshRenderer.bounds.extents,
			});
		}
		
		var instanceDataSize = Marshal.SizeOf(typeof(IndirectInstanceData));
		instanceDataBuffer = new ComputeBuffer(allRenderers.Length, instanceDataSize, ComputeBufferType.Default);
		visibilityBuffer = new ComputeBuffer(allRenderers.Length, sizeof(uint), ComputeBufferType.Default);
		
		occlusionKernelId = occlusionCS.FindKernel("CSMain");
		
		instanceDataBuffer.SetData(instanceData);
		
		occlusionCS.SetBuffer(occlusionKernelId, Shader.PropertyToID("_InstanceDataBuffer"), instanceDataBuffer);
		occlusionCS.SetBuffer(occlusionKernelId, Shader.PropertyToID("_VisibilityBuffer"), visibilityBuffer);
		
		Debug.Log(SystemInfo.usesReversedZBuffer);
	}

	private void OnDisable()
	{
		visibilityBuffer?.Release();
		instanceDataBuffer?.Release();
	}

	private void LateUpdate()
	{
		CalculateVisibleInstances(Camera.main);
		RenderInstances();
	}
	
	private void RenderInstances()
	{
		
	}
	
	private void CalculateVisibleInstances(Camera cam)
    {
        if (cam == null) { return; }
        
        RecreateTextures(cam);
        
        Graphics.SetRenderTarget(hiZDepthTexture);
        GL.Clear(true, true, Color.clear);

        var camPosition = cam.transform.position;

        //Matrix4x4 m = mainCamera.transform.localToWorldMatrix;
        Matrix4x4 v = cam.worldToCameraMatrix;
        Matrix4x4 p = cam.projectionMatrix;
        var mvp = p * v;
        
        // Common setup
        occlusionCS.SetMatrix(MvpMatrixId, mvp);
        occlusionCS.SetVector(CamPositionId, camPosition);
        occlusionCS.SetInt(CountId, allRenderers.Length);
        
        var dispatchSize = Mathf.Max(1, Mathf.NextPowerOfTwo(allRenderers.Length));

        Profiler.BeginSample("Pass 1");
        {
	        occlusionCS.SetInt(ShouldOcclusionCullId, 0);
            occlusionCS.SetInt(ShouldCullInvisibleId, 0);
            
            occlusionCS.Dispatch(occlusionKernelId, dispatchSize, 1, 1);
            // force a pipeline stall
            visibilityBuffer.GetData(visibleInstances);

            for (var i = 0; i < visibleInstances.Length; i++)
            {
	            var isVisible = visibleInstances[i];
	            //allRenderers[i].forceRenderingOff = isVisible == 0;
	            allRenderers[i].shadowCastingMode = isVisible == 0 ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
            }
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
        cam.RenderWithShader(depthShader, "RenderType");
        cam.targetTexture = null;
        cam.renderingPath = RenderingPath.DeferredShading;
        Profiler.EndSample();

        Profiler.BeginSample("Downsample");
        depthDownSampler.DownSample(depthTexture, hiZDepthTexture);
        Profiler.EndSample();
        
        Profiler.BeginSample("Pass 2");
        {
	        occlusionCS.SetInt(ShouldOcclusionCullId, 1);
	        occlusionCS.SetInt(ShouldCullInvisibleId, 1);
            
	        occlusionCS.Dispatch(occlusionKernelId, dispatchSize, 1, 1);
	        // force a pipeline stall
	        visibilityBuffer.GetData(visibleInstances);

	        for (var i = 0; i < visibleInstances.Length; i++)
	        {
		        var isVisible = visibleInstances[i];
		        allRenderers[i].shadowCastingMode = isVisible == 0 ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
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
			
			hiZDepthTexture = new RenderTexture(size, size, 0, RenderTextureFormat.RHalf, RenderTextureReadWrite.Linear);
			hiZDepthTexture.filterMode = FilterMode.Point;
			hiZDepthTexture.useMipMap = true;
			hiZDepthTexture.autoGenerateMips = false;
			hiZDepthTexture.Create();
			hiZDepthTexture.hideFlags = HideFlags.HideAndDontSave;
			
			occlusionCS.SetVector(HiZTextureSize, new Vector4(size, size));
			occlusionCS.SetTexture(occlusionKernelId, HiZTextureId, hiZDepthTexture);
		}
	}
}