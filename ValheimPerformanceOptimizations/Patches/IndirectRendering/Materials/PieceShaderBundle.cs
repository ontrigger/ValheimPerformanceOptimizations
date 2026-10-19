using System.Linq;
using UnityEngine;
using VPO = ValheimPerformanceOptimizations.ValheimPerformanceOptimizations;

namespace ValheimPerformanceOptimizations.Patches.IndirectRendering;

internal static class PieceShaderBundle
{
	private const string BundleName = "piece_shader";
	private const string ShaderName = "VPO/Piece";
	private const string StaticRockShaderName = "VPO/StaticRock";
	private const string VegetationShaderName = "VPO/Vegetation";
	private const string CreatureShaderName = "VPO/Creature";
	private const string StandardShaderName = "VPO/Standard";
	private const string SkyDepthShaderName = "Hidden/VPO/SkyDepth";
	private const string MultiDrawShadowCasterShaderName = "Hidden/VPO/MultiDrawShadowCaster";

	private static AssetBundle _bundle;

	internal static Shader Shader { get; private set; }
	internal static Shader StaticRockShader { get; private set; }
	internal static Shader VegetationShader { get; private set; }
	internal static Shader CreatureShader { get; private set; }
	internal static Shader StandardShader { get; private set; }
	internal static Shader SkyDepthShader { get; private set; }
	internal static Shader MultiDrawShadowCasterShader { get; private set; }

	internal static void Load()
	{
		if (_bundle)
		{
			return;
		}

		_bundle = AssetBundleHelper.GetAssetBundleFromResources(BundleName, out _);
		var shaders = _bundle.LoadAllAssets<Shader>();
		Shader = shaders.Single(shader => shader.name == ShaderName);
		StaticRockShader = shaders.Single(shader => shader.name == StaticRockShaderName);
		VegetationShader = shaders.Single(shader => shader.name == VegetationShaderName);
		CreatureShader = shaders.Single(shader => shader.name == CreatureShaderName);
		StandardShader = shaders.Single(shader => shader.name == StandardShaderName);
		SkyDepthShader = shaders.Single(shader => shader.name == SkyDepthShaderName);
		MultiDrawShadowCasterShader = shaders.Single(shader => shader.name == MultiDrawShadowCasterShaderName);
#if DEBUG
		VPO.Logger.LogInfo(
			$"Loaded {ShaderName}, {StaticRockShaderName}, {VegetationShaderName}, {CreatureShaderName}, " +
			$"{StandardShaderName}, {SkyDepthShaderName} and " +
			$"{MultiDrawShadowCasterShaderName} from the " +
			$"embedded {BundleName} asset bundle.");
#endif
	}
}
