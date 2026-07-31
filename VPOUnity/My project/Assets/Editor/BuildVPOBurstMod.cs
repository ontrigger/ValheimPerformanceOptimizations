using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Builds a Standalone player and copies the managed + Burst AOT libraries
/// for use with BurstRuntime.LoadAdditionalLibrary (see Burst modding support docs).
/// </summary>
public static class BuildVPOBurstMod
{
	private const string ModName = "VPOBurst";
	private const string ManagedAssemblyName = "VPOBurst_Managed";

	[MenuItem("VPO/Build Burst Mod (Win x64)")]
	public static void BuildGame()
	{
		var projectFolder = Path.Combine(Application.dataPath, "..");
		var buildFolder = Path.Combine(projectFolder, "PluginTemp");
		var defaultOutput = Path.GetFullPath(Path.Combine(projectFolder, "..", "..", "ValheimPerformanceOptimizations", "BurstOutput"));

		var path = EditorUtility.SaveFolderPanel("Choose Final Mod Location", defaultOutput, "");
		if (string.IsNullOrEmpty(path))
		{
			return;
		}

		FileUtil.DeleteFileOrDirectory(buildFolder);
		Directory.CreateDirectory(buildFolder);

		var report = BuildPipeline.BuildPlayer(
			new[] { "Assets/Scenes/SampleScene.unity" },
			Path.Combine(buildFolder, $"{ModName}.exe"),
			BuildTarget.StandaloneWindows64,
			BuildOptions.Development);

		if (report.summary.result != BuildResult.Succeeded)
		{
			Debug.LogError($"VPO Burst mod build failed: {report.summary.result}");
			return;
		}

		Directory.CreateDirectory(path);

		var managedDest = Path.Combine(path, $"{ManagedAssemblyName}.dll");
		var managedSrc = Path.Combine(buildFolder, $"{ModName}_Data/Managed/{ManagedAssemblyName}.dll");
		FileUtil.DeleteFileOrDirectory(managedDest);
		if (!File.Exists(managedDest))
		{
			FileUtil.CopyFileOrDirectory(managedSrc, managedDest);
		}
		else
		{
			Debug.LogWarning($"Couldn't update managed dll, {managedDest} is it currently in use?");
		}

		var burstedDest = Path.Combine(path, $"{ModName}_win_x86_64.dll");
		var burstedSrc = Path.Combine(buildFolder, $"{ModName}_Data/Plugins/x86_64/lib_burst_generated.dll");
		FileUtil.DeleteFileOrDirectory(burstedDest);
		if (!File.Exists(burstedDest))
		{
			FileUtil.CopyFileOrDirectory(burstedSrc, burstedDest);
		}
		else
		{
			Debug.LogWarning($"Couldn't update bursted dll, {burstedDest} is it currently in use?");
		}

		// Also copy Unity.Burst managed dependency needed by Valheim (game does not ship it).
		var burstManagedSrc = Path.Combine(buildFolder, $"{ModName}_Data/Managed/Unity.Burst.dll");
		var burstManagedDest = Path.Combine(path, "Unity.Burst.dll");
		if (File.Exists(burstManagedSrc))
		{
			FileUtil.DeleteFileOrDirectory(burstManagedDest);
			FileUtil.CopyFileOrDirectory(burstManagedSrc, burstManagedDest);
		}

		var burstUnsafeSrc = Path.Combine(buildFolder, $"{ModName}_Data/Managed/Unity.Burst.Unsafe.dll");
		var burstUnsafeDest = Path.Combine(path, "Unity.Burst.Unsafe.dll");
		if (File.Exists(burstUnsafeSrc))
		{
			FileUtil.DeleteFileOrDirectory(burstUnsafeDest);
			FileUtil.CopyFileOrDirectory(burstUnsafeSrc, burstUnsafeDest);
		}

		Debug.Log($"VPO Burst mod artifacts copied to: {path}");
	}
}
