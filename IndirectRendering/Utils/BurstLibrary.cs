using System.IO;
using IndirectRendering.Burst;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace IndirectRendering;

/// <summary>
/// Loads the Burst-compiled batch cull jobs, which ship next to this assembly.
/// Without them the jobs still run, as managed code.
/// </summary>
internal static class BurstLibrary
{
	private static bool _initialized;

	internal static bool JobsAreBursted { get; private set; }

	internal static void EnsureLoaded()
	{
		if (_initialized)
		{
			return;
		}

		_initialized = true;
		var fileName = Application.platform is RuntimePlatform.LinuxPlayer or RuntimePlatform.LinuxServer
			? "IndirectRendering.Burst_linux_x86_64.so"
			: "IndirectRendering.Burst_win_x86_64.dll";
		var directory = Path.GetDirectoryName(typeof(BurstLibrary).Assembly.Location);
		var path = string.IsNullOrEmpty(directory) ? fileName : Path.Combine(directory, fileName);
		if (!File.Exists(path))
		{
			Debug.LogWarning($"IndirectRendering: missing {fileName} next to IndirectRendering.dll.");
		}
		else if (!BurstRuntime.LoadAdditionalLibrary(path))
		{
			Debug.LogWarning($"IndirectRendering: BurstRuntime.LoadAdditionalLibrary failed for {path}.");
		}

		JobsAreBursted = ProbeIsBursted();
		if (JobsAreBursted)
		{
			Debug.Log("IndirectRendering: batch cull jobs are Burst compiled.");
		}
		else
		{
			Debug.LogWarning("IndirectRendering: batch cull jobs run as managed code.");
		}
	}

	private static bool ProbeIsBursted()
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
