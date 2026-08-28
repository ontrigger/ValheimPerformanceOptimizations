using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace ValheimPerformanceOptimizations
{
	[HarmonyPatch]
	internal static class ModCompatibility
	{
		internal static bool IsHDTerrainPresent =>
			Chainloader.PluginInfos.ContainsKey(ValheimPerformanceOptimizations.HDTerrain);

		internal static void Initialize()
		{
			
		}

		[MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
		private static void InitTerrainCompat()
		{
			
		}
	}
}
