using System.IO;
using System.Reflection;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using ValheimPerformanceOptimizations.Burst;

namespace ValheimPerformanceOptimizations;

public static class BurstLoader
{
	public const string WindowsBurstLibraryFileName = "ValheimPerformanceOptimizations.Burst_win_x86_64.dll";
	public const string LinuxBurstLibraryFileName = "ValheimPerformanceOptimizations.Burst_linux_x86_64.so";

	public static bool LibraryLoaded { get; private set; }
	public static bool JobsAreBursted { get; private set; }

	public static void Initialize()
	{
		var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
		if (string.IsNullOrEmpty(pluginDir))
		{
			ValheimPerformanceOptimizations.Logger.LogWarning("VPO Burst: could not resolve plugin directory");
			return;
		}

		var burstLibraryFileName = GetBurstLibraryFileName();
		if (burstLibraryFileName == null)
		{
			ValheimPerformanceOptimizations.Logger.LogWarning(
				$"VPO Burst: unsupported Unity platform '{Application.platform}'");
			return;
		}

		var burstPath = Path.Combine(pluginDir, burstLibraryFileName);
		if (!File.Exists(burstPath))
		{
			ValheimPerformanceOptimizations.Logger.LogWarning(
				$"VPO Burst: missing '{burstLibraryFileName}' next to the plugin.");
			return;
		}

		LibraryLoaded = BurstRuntime.LoadAdditionalLibrary(burstPath);
		if (!LibraryLoaded)
		{
			ValheimPerformanceOptimizations.Logger.LogWarning(
				$"VPO Burst: BurstRuntime.LoadAdditionalLibrary failed for '{burstPath}'");
		}

		JobsAreBursted = ProbeIsBursted();
		if (JobsAreBursted)
		{
			ValheimPerformanceOptimizations.Logger.LogInfo("VPO Burst: Burst jobs are enabled");
		}
		else
		{
			ValheimPerformanceOptimizations.Logger.LogWarning("VPO Burst: Burst jobs are disabled");
		}
	}

	private static string GetBurstLibraryFileName()
	{
		return Application.platform switch
		{
			RuntimePlatform.WindowsPlayer or RuntimePlatform.WindowsServer => WindowsBurstLibraryFileName,
			RuntimePlatform.LinuxPlayer or RuntimePlatform.LinuxServer => LinuxBurstLibraryFileName,
			_ => null,
		};
	}

	public static bool ProbeIsBursted()
	{
		var flag = new NativeArray<int>(1, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
		try
		{
			new CheckBurstedJob { Flag = flag }.Run();
			return flag[0] == 1;
		}
		finally
		{
			flag.Dispose();
		}
	}
}
