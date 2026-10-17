using System.IO;
using System.Linq;
using UnityEngine;

namespace IndirectRendering;

/// <summary>
/// The renderer's own shaders: PieceFrustumCull.compute and
/// Hidden/VPO/ShadowCascadeCapture. They ship in the indirect_renderer bundle
/// embedded in this assembly. A game on another Unity version can rebuild
/// that bundle with its version and pass FromBundle's result in
/// IndirectRendererSettings.Shaders.
/// </summary>
public sealed class IndirectRendererShaders
{
	private const string EmbeddedBundleName = "indirect_renderer";
	private const string FrustumCullShaderName = "PieceFrustumCull";
	private const string ShadowCascadeCaptureShaderName = "Hidden/VPO/ShadowCascadeCapture";

	// An asset bundle can only be loaded once, so every renderer shares these.
	private static IndirectRendererShaders _embedded;

	public static IndirectRendererShaders FromBundle(AssetBundle bundle)
	{
		return new IndirectRendererShaders(
			bundle.LoadAllAssets<ComputeShader>().Single(shader => shader.name == FrustumCullShaderName),
			bundle.LoadAllAssets<Shader>().Single(shader => shader.name == ShadowCascadeCaptureShaderName));
	}

	internal static IndirectRendererShaders LoadEmbedded()
	{
		if (_embedded != null)
		{
			return _embedded;
		}

		var assembly = typeof(IndirectRendererShaders).Assembly;
		var resourceName = assembly.GetManifestResourceNames().Single(name => name.EndsWith(EmbeddedBundleName));
		using var stream = assembly.GetManifestResourceStream(resourceName);
		using var memory = new MemoryStream();
		stream.CopyTo(memory);
		_embedded = FromBundle(AssetBundle.LoadFromMemory(memory.ToArray()));
		return _embedded;
	}

	public IndirectRendererShaders(ComputeShader frustumCullShader, Shader shadowCascadeCaptureShader)
	{
		FrustumCullShader = frustumCullShader;
		ShadowCascadeCaptureShader = shadowCascadeCaptureShader;
		ShadowFrustumCullKernel = frustumCullShader.FindKernel("ShadowFrustumCull");
		PrepareShadowCascadeDrawsKernel = frustumCullShader.FindKernel("PrepareShadowCascadeDraws");
		ClearShadowReceiverMapKernel = frustumCullShader.FindKernel("ClearShadowReceiverMap");
		BuildShadowDepthTilesKernel = frustumCullShader.FindKernel("BuildShadowDepthTiles");
		ScatterShadowReceiversKernel = frustumCullShader.FindKernel("ScatterShadowReceivers");
		BuildShadowReceiverMipsKernel = frustumCullShader.FindKernel("BuildShadowReceiverMips");
		ViewCullKernel = frustumCullShader.FindKernel("ViewCull");
		ClearInstanceCountsKernel = frustumCullShader.FindKernel("ClearInstanceCounts");
		ScatterInstancesKernel = frustumCullShader.FindKernel("ScatterInstances");
		CopyInstancesKernel = frustumCullShader.FindKernel("CopyInstances");
		ShadowFrustumCullGroupSize = GetThreadGroupSize(ShadowFrustumCullKernel).x;
		ViewCullGroupSize = GetThreadGroupSize(ViewCullKernel).x;
		ClearInstanceCountsGroupSize = GetThreadGroupSize(ClearInstanceCountsKernel).x;
		ScatterInstancesGroupSize = GetThreadGroupSize(ScatterInstancesKernel).x;
		CopyInstancesGroupSize = GetThreadGroupSize(CopyInstancesKernel).x;
		PrepareShadowCascadeDrawsGroupSize = GetThreadGroupSize(PrepareShadowCascadeDrawsKernel).x;
		ClearShadowReceiverMapGroupSize = GetThreadGroupSize(ClearShadowReceiverMapKernel).x;
		ScatterShadowReceiversGroupSize = GetThreadGroupSize(ScatterShadowReceiversKernel);
	}

	internal ComputeShader FrustumCullShader { get; }
	internal Shader ShadowCascadeCaptureShader { get; }
	internal int ShadowFrustumCullKernel { get; }
	internal int PrepareShadowCascadeDrawsKernel { get; }
	internal int ClearShadowReceiverMapKernel { get; }
	internal int BuildShadowDepthTilesKernel { get; }
	internal int ScatterShadowReceiversKernel { get; }
	internal int BuildShadowReceiverMipsKernel { get; }
	internal int ViewCullKernel { get; }
	internal int ClearInstanceCountsKernel { get; }
	internal int ScatterInstancesKernel { get; }
	internal int CopyInstancesKernel { get; }
	internal int ShadowFrustumCullGroupSize { get; }
	internal int ViewCullGroupSize { get; }
	internal int ClearInstanceCountsGroupSize { get; }
	internal int ScatterInstancesGroupSize { get; }
	internal int CopyInstancesGroupSize { get; }
	internal int PrepareShadowCascadeDrawsGroupSize { get; }
	internal int ClearShadowReceiverMapGroupSize { get; }
	internal Vector2Int ScatterShadowReceiversGroupSize { get; }

	private Vector2Int GetThreadGroupSize(int kernel)
	{
		FrustumCullShader.GetKernelThreadGroupSizes(kernel, out var x, out var y, out _);
		return new Vector2Int((int)x, (int)y);
	}
}
