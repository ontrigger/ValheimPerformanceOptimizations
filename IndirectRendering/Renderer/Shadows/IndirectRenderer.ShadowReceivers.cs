using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace IndirectRendering;

public sealed partial class IndirectRenderer
{
	private static readonly int ShadowDepthTilesId = Shader.PropertyToID("_ShadowDepthTiles");
	private const string CameraDepthTextureName = "_CameraDepthTexture";
	private static readonly int CameraDepthTextureId = Shader.PropertyToID(CameraDepthTextureName);

	[StructLayout(LayoutKind.Sequential)]
	private struct ShadowDepthTile
	{
		internal uint MinimumDepth;
		internal uint MaximumDepth;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ShadowReceiverParameters
	{
		internal Matrix4x4 InverseViewProjection;
		internal Vector4 LightBasisX;
		internal Vector4 LightBasisY;
		internal Vector4 LightBasisZ;
		internal Vector4 DepthSizeAndTileSize;
		internal Vector4 TileCountAndSettings;
	}

	private const int ShadowReceiverMapResolution = 128;
	private const int ShadowReceiverMapCascadeStride
		= (ShadowReceiverMapResolution * ShadowReceiverMapResolution * 4 - 1) / 3;
	private const int ShadowReceiverMapElementCount = ShadowReceiverMapCascadeStride * MaxDirectionalCascades;
	private const int ShadowReceiverDepthTileSize = 16;
	private const float ShadowReceiverDepthBias = 0.25f;

	private readonly ShadowReceiverParameters[] shadowReceiverParameters = new ShadowReceiverParameters[1];

	private GraphicsBuffer shadowReceiverParametersBuffer;
	private GraphicsBuffer shadowReceiverMapBuffer;
	private GraphicsBuffer shadowDepthTilesBuffer;

	private int shadowReceiverTileCountX;
	private int shadowReceiverTileCountY;
	private float shadowReceiverShadowFar;

	private void RecordShadowReceiverMap(CommandBuffer commandBuffer, ComputeShader computeShader)
	{
		commandBuffer.BeginSample("VPO Shadow Receiver Map Clear");
		var clearKernel = shaders.ClearShadowReceiverMapKernel;
		commandBuffer.SetComputeBufferParam(
			computeShader, clearKernel, ShadowReceiverMapId, shadowReceiverMapBuffer);
		commandBuffer.DispatchCompute(
			computeShader,
			clearKernel,
			ComputeDispatch.GetThreadGroupCount(
				ShadowReceiverMapCascadeStride * shadowCascadeCount, shaders.ClearShadowReceiverMapGroupSize),
			1,
			1);
		commandBuffer.EndSample("VPO Shadow Receiver Map Clear");

		commandBuffer.BeginSample("VPO Shadow Receiver Depth Tiles");
		var depthTileKernel = shaders.BuildShadowDepthTilesKernel;
		commandBuffer.SetComputeBufferParam(
			computeShader, depthTileKernel, ShadowReceiverParametersId, shadowReceiverParametersBuffer);
		commandBuffer.SetComputeBufferParam(
			computeShader, depthTileKernel, ShadowDepthTilesId, shadowDepthTilesBuffer);
		commandBuffer.SetComputeTextureParam(
			computeShader,
			depthTileKernel,
			CameraDepthTextureId,
			BuiltinRenderTextureType.ResolvedDepth);
		commandBuffer.DispatchCompute(
			computeShader,
			depthTileKernel,
			shadowReceiverTileCountX,
			shadowReceiverTileCountY,
			1);
		commandBuffer.EndSample("VPO Shadow Receiver Depth Tiles");

		commandBuffer.BeginSample("VPO Shadow Receiver Scatter");
		var scatterKernel = shaders.ScatterShadowReceiversKernel;
		commandBuffer.SetComputeBufferParam(
			computeShader, scatterKernel, ShadowReceiverParametersId, shadowReceiverParametersBuffer);
		commandBuffer.SetComputeBufferParam(
			computeShader, scatterKernel, ShadowDepthTilesId, shadowDepthTilesBuffer);
		commandBuffer.SetComputeBufferParam(
			computeShader, scatterKernel, ShadowReceiverMapId, shadowReceiverMapBuffer);
		commandBuffer.SetComputeBufferParam(
			computeShader, scatterKernel, ShadowCascadeSpheresId, shadowCascadeSpheresBuffer);
		commandBuffer.DispatchCompute(
			computeShader,
			scatterKernel,
			ComputeDispatch.GetThreadGroupCount(
				shadowReceiverTileCountX, shaders.ScatterShadowReceiversGroupSize.x),
			ComputeDispatch.GetThreadGroupCount(
				shadowReceiverTileCountY, shaders.ScatterShadowReceiversGroupSize.y),
			1);
		commandBuffer.EndSample("VPO Shadow Receiver Scatter");

		commandBuffer.BeginSample("VPO Shadow Receiver Mips");
		var mipKernel = shaders.BuildShadowReceiverMipsKernel;
		commandBuffer.SetComputeBufferParam(
			computeShader, mipKernel, ShadowReceiverParametersId, shadowReceiverParametersBuffer);
		commandBuffer.SetComputeBufferParam(
			computeShader, mipKernel, ShadowReceiverMapId, shadowReceiverMapBuffer);
		// One group per cascade builds that cascade's whole mip chain.
		commandBuffer.DispatchCompute(computeShader, mipKernel, shadowCascadeCount, 1, 1);
		commandBuffer.EndSample("VPO Shadow Receiver Mips");
	}

	private void UpdateShadowReceiverResources(
		Camera camera,
		Light directionalLight,
		bool hasShadowWork)
	{
		if (!hasShadowWork)
		{
			return;
		}

		var depthWidth = Mathf.Max(1, camera.pixelWidth);
		var depthHeight = Mathf.Max(1, camera.pixelHeight);
		EnsureShadowDepthTilesBuffer(depthWidth, depthHeight);
		shadowCascadeSpheresBuffer.SetData(shadowCascadeSpheresReset);

		var lightTransform = directionalLight.transform;
		var lightBasisX = lightTransform.right;
		var lightBasisY = lightTransform.up;
		var lightBasisZ = lightTransform.forward;
		// Raw depth at the shadow distance, and the sign that makes nearer pixels
		// compare greater (positive for reversed Z), let the depth-tile kernel
		// reject far and sky pixels without reconstructing positions.
		var gpuProjection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, false);
		var rawShadowFar = ProjectRawDepth(
			gpuProjection, Mathf.Max(shadowReceiverShadowFar, camera.nearClipPlane * 2f));
		var rawDepthDirection = ProjectRawDepth(gpuProjection, camera.nearClipPlane) >= rawShadowFar ? 1f : -1f;
		var receiverParameters = new ShadowReceiverParameters
		{
			InverseViewProjection = (gpuProjection * camera.worldToCameraMatrix).inverse,
			LightBasisX = new Vector4(lightBasisX.x, lightBasisX.y, lightBasisX.z, rawDepthDirection),
			LightBasisY = new Vector4(lightBasisY.x, lightBasisY.y, lightBasisY.z, 0f),
			LightBasisZ = new Vector4(lightBasisZ.x, lightBasisZ.y, lightBasisZ.z, 0f), DepthSizeAndTileSize
				= new Vector4(
					depthWidth,
					depthHeight,
					ShadowReceiverDepthTileSize,
					rawShadowFar),
			TileCountAndSettings = new Vector4(
				shadowReceiverTileCountX,
				shadowReceiverTileCountY,
				ShadowReceiverDepthBias,
				0f),
		};
		shadowReceiverParameters[0] = receiverParameters;
		shadowReceiverParametersBuffer.SetData(shadowReceiverParameters);
	}

	private static float ProjectRawDepth(Matrix4x4 gpuProjection, float viewDepth)
	{
		var clip = gpuProjection * new Vector4(0f, 0f, -viewDepth, 1f);
		return clip.z / clip.w;
	}

	private void EnsureShadowDepthTilesBuffer(int depthWidth, int depthHeight)
	{
		var tileCountX = Mathf.CeilToInt(depthWidth / (float)ShadowReceiverDepthTileSize);
		var tileCountY = Mathf.CeilToInt(depthHeight / (float)ShadowReceiverDepthTileSize);
		var tileCount = checked(tileCountX * tileCountY);
		if (shadowDepthTilesBuffer == null || shadowDepthTilesBuffer.count != tileCount)
		{
			shadowDepthTilesBuffer?.Release();
			shadowDepthTilesBuffer = new GraphicsBuffer(
				GraphicsBuffer.Target.Structured,
				tileCount,
				Marshal.SizeOf<ShadowDepthTile>());
			InvalidateShadowRecordings();
		}

		shadowReceiverTileCountX = tileCountX;
		shadowReceiverTileCountY = tileCountY;
	}
}
