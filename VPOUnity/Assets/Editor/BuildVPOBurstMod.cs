using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;


public static class BuildVPOBurstMod
{
	private static readonly BurstMod ValheimPerformanceOptimizationsBurst = new(
		"ValheimPerformanceOptimizations.Burst",
		"ValheimPerformanceOptimizations.Burst",
		"VPO_BURST_BUILD",
		Path.Combine("ValheimPerformanceOptimizations", "BurstOutput"));

	private static readonly BurstMod IndirectRenderingBurst = new(
		"IndirectRendering.Burst",
		"IndirectRendering.Burst",
		"INDIRECT_RENDERING_BURST_BUILD",
		Path.Combine("IndirectRendering", "BurstOutput"));

	private static readonly Platform[] Platforms =
	{
		new(BuildTarget.StandaloneLinux64, "linux_x86_64", ".x86_64", "LinuxPlayerBuildProgram", ".so"),
		new(BuildTarget.StandaloneWindows64, "win_x86_64", ".exe", "WinPlayerBuildProgram", ".dll"),
	};

	[MenuItem("VPO/Build Valheim Performance Optimizations Burst (Win + Linux x64)")]
	public static void BuildValheimPerformanceOptimizations()
	{
		Build(ValheimPerformanceOptimizationsBurst);
	}

	[MenuItem("VPO/Build Indirect Rendering Burst (Win + Linux x64)")]
	public static void BuildIndirectRendering()
	{
		Build(IndirectRenderingBurst);
	}

	/// <summary>
	/// .\unity.exe -batchmode -quit -projectPath "..." -executeMethod BuildVPOBurstMod.BuildValheimPerformanceOptimizationsBatch
	/// </summary>
	public static void BuildValheimPerformanceOptimizationsBatch()
	{
		Build(ValheimPerformanceOptimizationsBurst);
	}

	/// <summary>
	/// .\unity.exe -batchmode -quit -projectPath "..." -executeMethod BuildVPOBurstMod.BuildIndirectRenderingBatch
	/// </summary>
	public static void BuildIndirectRenderingBatch()
	{
		Build(IndirectRenderingBurst);
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

	private sealed class Platform
	{
		internal readonly BuildTarget Target;
		internal readonly string LibrarySuffix;
		internal readonly string ExecutableExtension;
		internal readonly string BuildProgram;
		internal readonly string LibraryExtension;

		internal Platform(
			BuildTarget target,
			string librarySuffix,
			string executableExtension,
			string buildProgram,
			string libraryExtension)
		{
			Target = target;
			LibrarySuffix = librarySuffix;
			ExecutableExtension = executableExtension;
			BuildProgram = buildProgram;
			LibraryExtension = libraryExtension;
		}
	}

	private static void Build(BurstMod mod)
	{
		var projectFolder = Path.Combine(Application.dataPath, "..");
		var output = Path.GetFullPath(Path.Combine(projectFolder, "..", mod.OutputFolder));
		foreach (var platform in Platforms)
		{
			if (!BuildTo(mod, platform, output))
			{
				return;
			}
		}
	}

	private static bool BuildTo(BurstMod mod, Platform platform, string path)
	{
		var projectFolder = Path.Combine(Application.dataPath, "..");
		var buildFolder = Path.Combine(projectFolder, "PluginTemp", mod.Name, platform.LibrarySuffix);

		FileUtil.DeleteFileOrDirectory(buildFolder);
		Directory.CreateDirectory(buildFolder);
		// Bee does not notice that the define changed which assemblies Burst
		// compiles, so without this every mod gets the last mod's library.
		FileUtil.DeleteFileOrDirectory(Path.Combine(
			projectFolder, "Library", "Bee", "artifacts", platform.BuildProgram, "AsyncPluginsFromLinker"));
		var buildStart = System.DateTime.Now;

		var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
		{
			scenes = new[] { "Assets/Scenes/SampleScene.unity" },
			locationPathName = Path.Combine(buildFolder, mod.Name + platform.ExecutableExtension),
			target = platform.Target,
			options = BuildOptions.Development,
			extraScriptingDefines = new[] { mod.BuildDefine },
		});

		if (report.summary.result != BuildResult.Succeeded)
		{
			Fail($"{mod.Name} {platform.Target} Burst build failed: {report.summary.result}");
			return false;
		}

		var dataFolder = Path.Combine(buildFolder, $"{mod.Name}_Data");
		// Windows puts native plugins in Plugins/x86_64, Linux directly in Plugins.
		var burstLibraryName = "lib_burst_generated" + platform.LibraryExtension;
		var burstLibraries = Directory.GetFiles(
			Path.Combine(dataFolder, "Plugins"), burstLibraryName, SearchOption.AllDirectories);
		if (burstLibraries.Length == 0 || File.GetLastWriteTime(burstLibraries[0]) < buildStart)
		{
			Fail($"{mod.Name} {platform.Target} Burst build did not regenerate {burstLibraryName}.");
			return false;
		}

		Directory.CreateDirectory(path);
		Copy(
			Path.Combine(dataFolder, "Managed", $"{mod.ManagedAssemblyName}.dll"),
			Path.Combine(path, $"{mod.ManagedAssemblyName}.dll"));
		Copy(burstLibraries[0], Path.Combine(path, $"{mod.Name}_{platform.LibrarySuffix}{platform.LibraryExtension}"));
		// Unity.Burst managed dependencies needed by the game, which does not ship them.
		CopyIfExists(Path.Combine(dataFolder, "Managed", "Unity.Burst.dll"), Path.Combine(path, "Unity.Burst.dll"));
		CopyIfExists(
			Path.Combine(dataFolder, "Managed", "Unity.Burst.Unsafe.dll"),
			Path.Combine(path, "Unity.Burst.Unsafe.dll"));

		Debug.Log($"{mod.Name} {platform.Target} Burst artifacts copied to: {path}");
		return true;
	}

	private static void Fail(string message)
	{
		Debug.LogError(message);
		if (Application.isBatchMode)
		{
			EditorApplication.Exit(1);
		}
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
