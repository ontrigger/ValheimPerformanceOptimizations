using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Builds a Standalone player and copies the managed + Burst AOT libraries
/// for use with BurstRuntime.LoadAdditionalLibrary (see Burst modding support docs).
/// A player build compiles every Burst assembly into one native library, so
/// each mod is built on its own, with a define that excludes the others'
/// assemblies through their asmdef define constraints.
/// </summary>
public static class BuildVPOBurstMod
{
	// Where the player build writes lib_burst_generated.dll, shared by every mod.
	private static readonly string BurstLibraryCacheFolder = Path.Combine(
		"Library", "Bee", "artifacts", "WinPlayerBuildProgram", "AsyncPluginsFromLinker");

	private static readonly BurstMod VPOBurst = new(
		"VPOBurst",
		"VPOBurst_Managed",
		"VPO_BURST_BUILD",
		Path.Combine("ValheimPerformanceOptimizations", "BurstOutput"));

	private static readonly BurstMod IndirectRenderingBurst = new(
		"IndirectRendering.Burst",
		"IndirectRendering.Burst",
		"INDIRECT_RENDERING_BURST_BUILD",
		Path.Combine("IndirectRendering", "BurstOutput"));

	[MenuItem("VPO/Build Burst Mod (Win x64)")]
	public static void BuildGame()
	{
		BuildWithPanel(VPOBurst);
	}

	[MenuItem("VPO/Build Indirect Rendering Burst (Win x64)")]
	public static void BuildIndirectRendering()
	{
		BuildWithPanel(IndirectRenderingBurst);
	}

	/// <summary>
	/// .\unity.exe -batchmode -quit -projectPath "..." -executeMethod BuildVPOBurstMod.BuildGameBatch
	/// </summary>
	public static void BuildGameBatch()
	{
		BuildTo(VPOBurst, GetDefaultOutput(VPOBurst));
	}

	/// <summary>
	/// .\unity.exe -batchmode -quit -projectPath "..." -executeMethod BuildVPOBurstMod.BuildIndirectRenderingBatch
	/// </summary>
	public static void BuildIndirectRenderingBatch()
	{
		BuildTo(IndirectRenderingBurst, GetDefaultOutput(IndirectRenderingBurst));
	}

	private sealed class BurstMod
	{
		internal readonly string Name;
		internal readonly string ManagedAssemblyName;
		internal readonly string BuildDefine;
		// Relative to the repository root.
		internal readonly string OutputFolder;

		internal BurstMod(string name, string managedAssemblyName, string buildDefine, string outputFolder)
		{
			Name = name;
			ManagedAssemblyName = managedAssemblyName;
			BuildDefine = buildDefine;
			OutputFolder = outputFolder;
		}
	}

	private static string GetDefaultOutput(BurstMod mod)
	{
		var projectFolder = Path.Combine(Application.dataPath, "..");
		return Path.GetFullPath(Path.Combine(projectFolder, "..", mod.OutputFolder));
	}

	private static void BuildWithPanel(BurstMod mod)
	{
		var path = EditorUtility.SaveFolderPanel("Choose Final Mod Location", GetDefaultOutput(mod), "");
		if (string.IsNullOrEmpty(path))
		{
			return;
		}

		BuildTo(mod, path);
	}

	private static void BuildTo(BurstMod mod, string path)
	{
		var projectFolder = Path.Combine(Application.dataPath, "..");
		var buildFolder = Path.Combine(projectFolder, "PluginTemp", mod.Name);

		FileUtil.DeleteFileOrDirectory(buildFolder);
		Directory.CreateDirectory(buildFolder);
		// Bee does not notice that the define changed which assemblies Burst
		// compiles, so without this every mod gets the last mod's library.
		FileUtil.DeleteFileOrDirectory(Path.Combine(projectFolder, BurstLibraryCacheFolder));
		var buildStart = System.DateTime.Now;

		var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
		{
			scenes = new[] { "Assets/Scenes/SampleScene.unity" },
			locationPathName = Path.Combine(buildFolder, $"{mod.Name}.exe"),
			target = BuildTarget.StandaloneWindows64,
			options = BuildOptions.Development,
			extraScriptingDefines = new[] { mod.BuildDefine },
		});

		if (report.summary.result != BuildResult.Succeeded)
		{
			Debug.LogError($"{mod.Name} Burst build failed: {report.summary.result}");
			if (Application.isBatchMode)
			{
				EditorApplication.Exit(1);
			}
			return;
		}

		var dataFolder = Path.Combine(buildFolder, $"{mod.Name}_Data");
		var burstLibrary = Path.Combine(dataFolder, "Plugins", "x86_64", "lib_burst_generated.dll");
		if (!File.Exists(burstLibrary) || File.GetLastWriteTime(burstLibrary) < buildStart)
		{
			Debug.LogError($"{mod.Name} Burst build did not regenerate lib_burst_generated.dll.");
			if (Application.isBatchMode)
			{
				EditorApplication.Exit(1);
			}
			return;
		}

		Directory.CreateDirectory(path);
		Copy(
			Path.Combine(dataFolder, "Managed", $"{mod.ManagedAssemblyName}.dll"),
			Path.Combine(path, $"{mod.ManagedAssemblyName}.dll"));
		Copy(burstLibrary, Path.Combine(path, $"{mod.Name}_win_x86_64.dll"));
		// Unity.Burst managed dependencies needed by the game, which does not ship them.
		CopyIfExists(Path.Combine(dataFolder, "Managed", "Unity.Burst.dll"), Path.Combine(path, "Unity.Burst.dll"));
		CopyIfExists(
			Path.Combine(dataFolder, "Managed", "Unity.Burst.Unsafe.dll"),
			Path.Combine(path, "Unity.Burst.Unsafe.dll"));

		Debug.Log($"{mod.Name} Burst artifacts copied to: {path}");
	}

	private static void Copy(string source, string destination)
	{
		FileUtil.DeleteFileOrDirectory(destination);
		if (File.Exists(destination))
		{
			Debug.LogWarning($"Couldn't update {destination}, is it currently in use?");
			return;
		}

		FileUtil.CopyFileOrDirectory(source, destination);
	}

	private static void CopyIfExists(string source, string destination)
	{
		if (File.Exists(source))
		{
			Copy(source, destination);
		}
	}
}
