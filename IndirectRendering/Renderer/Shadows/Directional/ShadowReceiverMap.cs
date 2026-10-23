using System.Runtime.InteropServices;
using IndirectRendering.Extensions;
using UnityEngine;
using UnityEngine.Rendering;

namespace IndirectRendering;

/// <summary>
/// Where in each directional cascade the camera's depth buffer has shadow
/// receivers, built on the GPU in the shadow pass from the camera depth's
/// tiles, so the shadow cull can skip casters that shadow nothing visible.
/// </summary>
internal sealed class ShadowReceiverMap
{
	private static readonly int ShadowDepthTilesId = Shader.PropertyToID("_ShadowDepthTiles");
	private static readonly int CameraDepthTextureId = Shader.PropertyToID("_CameraDepthTexture");
	private static readonly int ShadowReceiverParametersId = Shader.PropertyToID("_ShadowReceiverParameters");
	private static readonly int ShadowReceiverMapId = Shader.PropertyToID("_ShadowReceiverMap");
	private static readonly int ShadowCascadeSpheresId = Shader.PropertyToID("_ShadowCascadeSpheres");

	[StructLayout(LayoutKind.Sequential)]
	internal struct DepthTile
	{
		internal uint MinimumDepth;
		internal uint MaximumDepth;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct Parameters
	{
		internal Matrix4x4 InverseViewProjection;
		internal Vector4 LightBasisX;
		internal Vector4 LightBasisY;
		internal Vector4 LightBasisZ;
		internal Vector4 DepthSizeAndTileSize;
		internal Vector4 TileCountAndSettings;
	}

	private const int MapResolution = 128;
	private const int MapCascadeStride = (MapResolution * MapResolution * 4 - 1) / 3;
	private const int MapElementCount = MapCascadeStride * IndirectRenderer.MaxDirectionalCascades;
	private const int DepthTileSize = 16;
	private const float DepthBias = 0.25f;

	private readonly IndirectRendererShaders shaders;
	private readonly Parameters[] parameters = new Parameters[1];

	private GraphicsBuffer parametersBuffer;
	private GraphicsBuffer mapBuffer;
	private GraphicsBuffer depthTilesBuffer;

	private int tileCountX;
	private int tileCountY;

	internal ShadowReceiverMap(IndirectRendererShaders shaders)
	{
		this.shaders = shaders;
	}

	internal void EnsureBuffers()
	{
		if (parametersBuffer != null) { return; }

		parametersBuffer = new GraphicsBuffer(
			GraphicsBuffer.Target.Structured,
			1,
			Marshal.SizeOf<Parameters>());
		mapBuffer = new GraphicsBuffer(
			GraphicsBuffer.Target.Structured,
			MapElementCount,
			sizeof(uint));
	}

	/// <summary>
	/// Records building the map for cascadeCount cascades, whose spheres the
	/// cascade capture writes to cascadeSpheres.
	/// </summary>
	internal void Record(
		CommandBuffer commandBuffer,
		ComputeShader computeShader,
		int cascadeCount,
		GraphicsBuffer cascadeSpheres)
	{
		commandBuffer.BeginSample("VPO Shadow Receiver Map Clear");
		var clearKernel = shaders.ClearShadowReceiverMapKernel;
		commandBuffer.SetComputeBufferParam(
			computeShader, clearKernel, ShadowReceiverMapId, mapBuffer);
		commandBuffer.DispatchCompute(
			computeShader,
			clearKernel,
			ComputeDispatch.GetThreadGroupCount(
				MapCascadeStride * cascadeCount, shaders.ClearShadowReceiverMapGroupSize),
			1,
			1);
		commandBuffer.EndSample("VPO Shadow Receiver Map Clear");

		commandBuffer.BeginSample("VPO Shadow Receiver Depth Tiles");
		var depthTileKernel = shaders.BuildShadowDepthTilesKernel;
		commandBuffer.SetComputeBufferParam(
			computeShader, depthTileKernel, ShadowReceiverParametersId, parametersBuffer);
		commandBuffer.SetComputeBufferParam(
			computeShader, depthTileKernel, ShadowDepthTilesId, depthTilesBuffer);
		commandBuffer.SetComputeTextureParam(
			computeShader,
			depthTileKernel,
			CameraDepthTextureId,
			BuiltinRenderTextureType.ResolvedDepth);
		commandBuffer.DispatchCompute(
			computeShader,
			depthTileKernel,
			tileCountX,
			tileCountY,
			1);
		commandBuffer.EndSample("VPO Shadow Receiver Depth Tiles");

		commandBuffer.BeginSample("VPO Shadow Receiver Scatter");
		var scatterKernel = shaders.ScatterShadowReceiversKernel;
		commandBuffer.SetComputeBufferParam(
			computeShader, scatterKernel, ShadowReceiverParametersId, parametersBuffer);
		commandBuffer.SetComputeBufferParam(
			computeShader, scatterKernel, ShadowDepthTilesId, depthTilesBuffer);
		commandBuffer.SetComputeBufferParam(
			computeShader, scatterKernel, ShadowReceiverMapId, mapBuffer);
		commandBuffer.SetComputeBufferParam(
			computeShader, scatterKernel, ShadowCascadeSpheresId, cascadeSpheres);
		commandBuffer.DispatchCompute(
			computeShader,
			scatterKernel,
			ComputeDispatch.GetThreadGroupCount(
				tileCountX, shaders.ScatterShadowReceiversGroupSize.x),
			ComputeDispatch.GetThreadGroupCount(
				tileCountY, shaders.ScatterShadowReceiversGroupSize.y),
			1);
		commandBuffer.EndSample("VPO Shadow Receiver Scatter");

		commandBuffer.BeginSample("VPO Shadow Receiver Mips");
		var mipKernel = shaders.BuildShadowReceiverMipsKernel;
		commandBuffer.SetComputeBufferParam(
			computeShader, mipKernel, ShadowReceiverParametersId, parametersBuffer);
		commandBuffer.SetComputeBufferParam(
			computeShader, mipKernel, ShadowReceiverMapId, mapBuffer);
		// One group per cascade builds that cascade's whole mip chain.
		commandBuffer.DispatchCompute(computeShader, mipKernel, cascadeCount, 1, 1);
		commandBuffer.EndSample("VPO Shadow Receiver Mips");
	}

	internal void Bind(CommandBuffer commandBuffer, ComputeShader computeShader, int kernel)
	{
		commandBuffer.SetComputeBufferParam(
			computeShader, kernel, ShadowReceiverParametersId, parametersBuffer);
		commandBuffer.SetComputeBufferParam(
			computeShader, kernel, ShadowReceiverMapId, mapBuffer);
	}

	/// <summary>
	/// Returns whether the depth tiles buffer was
	/// replaced, which a recording of Record holds.
	/// </summary>
	internal bool TryUpdate(Camera camera, Light directionalLight, float shadowFar)
	{
		var depthWidth = Mathf.Max(1, camera.pixelWidth);
		var depthHeight = Mathf.Max(1, camera.pixelHeight);
		var tilesReplaced = EnsureDepthTilesBuffer(depthWidth, depthHeight);

		var lightTransform = directionalLight.transform;
		var lightBasisX = lightTransform.right;
		var lightBasisY = lightTransform.up;
		var lightBasisZ = lightTransform.forward;
		var gpuProjection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, false);
		var rawShadowFar = ProjectRawDepth(
			gpuProjection, Mathf.Max(shadowFar, camera.nearClipPlane * 2f));
		var rawDepthDirection = ProjectRawDepth(gpuProjection, camera.nearClipPlane) >= rawShadowFar ? 1f : -1f;
		parameters[0] = new Parameters
		{
			InverseViewProjection = (gpuProjection * camera.worldToCameraMatrix).inverse,
			LightBasisX = new Vector4(lightBasisX.x, lightBasisX.y, lightBasisX.z, rawDepthDirection),
			LightBasisY = new Vector4(lightBasisY.x, lightBasisY.y, lightBasisY.z, 0f),
			LightBasisZ = new Vector4(lightBasisZ.x, lightBasisZ.y, lightBasisZ.z, 0f), DepthSizeAndTileSize
				= new Vector4(
					depthWidth,
					depthHeight,
					DepthTileSize,
					rawShadowFar),
			TileCountAndSettings = new Vector4(
				tileCountX,
				tileCountY,
				DepthBias,
				0f),
		};
		parametersBuffer.SetData(parameters);
		return tilesReplaced;
	}

	private static float ProjectRawDepth(Matrix4x4 gpuProjection, float viewDepth)
	{
		var clip = gpuProjection * new Vector4(0f, 0f, -viewDepth, 1f);
		return clip.z / clip.w;
	}

	private bool EnsureDepthTilesBuffer(int depthWidth, int depthHeight)
	{
		tileCountX = Mathf.CeilToInt(depthWidth / (float)DepthTileSize);
		tileCountY = Mathf.CeilToInt(depthHeight / (float)DepthTileSize);
		var tileCount = checked(tileCountX * tileCountY);
		if (depthTilesBuffer != null && depthTilesBuffer.count == tileCount) { return false; }

		depthTilesBuffer?.Release();
		depthTilesBuffer = new GraphicsBuffer(
			GraphicsBuffer.Target.Structured,
			tileCount,
			Marshal.SizeOf<DepthTile>());
		return true;
	}

	internal void Release()
	{
		GraphicsBufferX.Release(ref parametersBuffer);
		GraphicsBufferX.Release(ref mapBuffer);
		GraphicsBufferX.Release(ref depthTilesBuffer);
		tileCountX = 0;
		tileCountY = 0;
	}
}
