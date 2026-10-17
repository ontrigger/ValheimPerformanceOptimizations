using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds VPO's piece_shader bundle, with its material shaders, and the
/// indirect renderer's own indirect_renderer bundle, with the shaders it runs
/// itself. Each goes to its project's AssetBundles folder.
/// </summary>
public static class BuildVPOAssetBundles
{
	private static readonly Bundle PieceShaderBundle = new(
		"piece_shader",
		Path.Combine("ValheimPerformanceOptimizations", "AssetBundles"),
		"Assets/Shaders/Piece.shader",
		"Assets/Shaders/StaticRock.shader",
		"Assets/Shaders/Vegetation.shader",
		"Assets/Shaders/Creature.shader",
		"Assets/Shaders/VPOStandard.shader",
		"Assets/Shaders/SkyDepth.shader");

	private static readonly Bundle IndirectRendererBundle = new(
		"indirect_renderer",
		Path.Combine("IndirectRendering", "AssetBundles"),
		"Assets/IndirectRendering/Shaders/PieceFrustumCull.compute",
		"Assets/IndirectRendering/Shaders/ShadowCascadeCapture.shader");

	[MenuItem("VPO/Build Piece Shader Asset Bundle")]
	public static void BuildPieceShaderBundle()
	{
		BuildTo(PieceShaderBundle, GetDefaultOutput(PieceShaderBundle));
	}

	[MenuItem("VPO/Build Indirect Renderer Asset Bundle")]
	public static void BuildIndirectRendererBundle()
	{
		BuildTo(IndirectRendererBundle, GetDefaultOutput(IndirectRendererBundle));
	}

	/// <summary>
	/// .\Unity.exe -batchmode -quit -projectPath "...\VPOUnity" -executeMethod BuildVPOAssetBundles.BuildPieceShaderBundleBatch
	/// </summary>
	public static void BuildPieceShaderBundleBatch()
	{
		BuildTo(PieceShaderBundle, GetDefaultOutput(PieceShaderBundle));
	}

	/// <summary>
	/// .\Unity.exe -batchmode -quit -projectPath "...\VPOUnity" -executeMethod BuildVPOAssetBundles.BuildIndirectRendererBundleBatch
	/// </summary>
	public static void BuildIndirectRendererBundleBatch()
	{
		BuildTo(IndirectRendererBundle, GetDefaultOutput(IndirectRendererBundle));
	}

	private sealed class Bundle
	{
		internal readonly string Name;
		// Relative to the repository root.
		internal readonly string OutputFolder;
		internal readonly string[] AssetPaths;

		internal Bundle(string name, string outputFolder, params string[] assetPaths)
		{
			Name = name;
			OutputFolder = outputFolder;
			AssetPaths = assetPaths;
		}
	}

	private static string GetDefaultOutput(Bundle bundle)
	{
		var projectFolder = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
		return Path.GetFullPath(Path.Combine(projectFolder, "..", bundle.OutputFolder));
	}

	private static void BuildTo(Bundle bundle, string output)
	{
		foreach (var assetPath in bundle.AssetPaths)
		{
			if (!AssetDatabase.LoadMainAssetAtPath(assetPath))
			{
				Fail($"Couldn't load {assetPath} for the {bundle.Name} asset bundle.");
				return;
			}
		}

		Directory.CreateDirectory(output);
		var build = new AssetBundleBuild { assetBundleName = bundle.Name, assetNames = bundle.AssetPaths };
		var manifest = BuildPipeline.BuildAssetBundles(
			output,
			new[] { build },
			BuildAssetBundleOptions.ChunkBasedCompression,
			BuildTarget.StandaloneWindows64);
		if (!manifest)
		{
			Fail($"The {bundle.Name} asset bundle build failed.");
			return;
		}

		Debug.Log($"The {bundle.Name} asset bundle was written to {Path.Combine(output, bundle.Name)}.");
	}

	private static void Fail(string message)
	{
		Debug.LogError(message);
		if (Application.isBatchMode)
		{
			EditorApplication.Exit(1);
		}
	}
}
