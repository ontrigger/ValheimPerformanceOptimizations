using System.IO;
using System.Reflection;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using VPOBurst;

namespace ValheimPerformanceOptimizations
{
	public static class BurstLoader
	{
		public const string BurstLibraryFileName = "VPOBurst_win_x86_64.dll";

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

			var burstPath = Path.Combine(pluginDir, BurstLibraryFileName);
			if (!File.Exists(burstPath))
			{
				ValheimPerformanceOptimizations.Logger.LogWarning(
					$"VPO Burst: missing '{BurstLibraryFileName}' next to the plugin.");
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
				ValheimPerformanceOptimizations.Logger.LogInfo($"VPO Burst: Burst jobs are enabled");
			}
			else
			{
				ValheimPerformanceOptimizations.Logger.LogWarning($"VPO Burst: Burst jobs are disabled");
			}
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
}
