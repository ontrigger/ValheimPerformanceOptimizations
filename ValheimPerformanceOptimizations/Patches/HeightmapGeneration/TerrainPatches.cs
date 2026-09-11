using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimPerformanceOptimizations.Patches.HeightmapGeneration;

using VPO = ValheimPerformanceOptimizations;
internal static class TerrainPatches
{
	private static ConfigEntry<bool> _threadedCollisionBakeEnabled;

	static TerrainPatches()
	{
		VPO.OnInitialized += Initialize;
	}

	private static void Initialize(ConfigFile configFile, Harmony harmony)
	{
		if (ModCompatibility.IsHDTerrainPresent)
		{
			VPO.Logger.LogInfo(
				"HDTerrain detected; skipping heightmap generation patches.");
			return;
		}

		harmony.PatchAll(typeof(HeightmapColorGenerationPatch));

		const string key = "Threaded terrain collision baking enabled";
		const string description =
			"Experimental: if enabled terrain is generated in parallel, this reduces lag spikes when moving through the world. This is an experimental feature, please report any issues that may occur.";
		_threadedCollisionBakeEnabled = configFile.Bind("General", key, true, description);

		if (_threadedCollisionBakeEnabled.Value)
		{
			harmony.PatchAll(typeof(ThreadedHeightmapCollisionBakePatch));
		}
	}
}
