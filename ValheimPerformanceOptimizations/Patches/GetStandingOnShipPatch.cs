using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;

namespace ValheimPerformanceOptimizations.Patches
{
    public static class GetStandingOnShipPatch
    {
        private static readonly ConditionalWeakTable<Character, CachedShipData> Data =
            new ConditionalWeakTable<Character, CachedShipData>();

        static GetStandingOnShipPatch()
        {
            ValheimPerformanceOptimizations.OnInitialized += Initialize;
        }

        private static void Initialize(ConfigFile configFile, Harmony harmony)
        {
            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(ValheimPerformanceOptimizations.ValheimRaftId))
            {
                harmony.PatchAll(typeof(GetStandingOnShipPatch));
            }
        }

        private static CachedShipData GetCachedShipData(this Character character)
        {
            return Data.GetOrCreateValue(character);
        }

        [HarmonyPatch(typeof(Character), nameof(Character.GetStandingOnShip))]
        private static bool Prefix(Character __instance, ref Ship __result)
        {
            if (!__instance.IsOnGround())
            {
                __result = null;
                return false;
            }

            if (__instance.m_lastGroundBody)
            {
                var cachedData = __instance.GetCachedShipData();

                if (cachedData.IsCached)
                {
                    __result = cachedData.LastShip;
                    return false;
                }

                cachedData.LastShip = __instance.m_lastGroundBody.GetComponent<Ship>();
                cachedData.IsCached = true;

                __result = cachedData.LastShip;
                return false;
            }

            __result = null;
            return false;
        }

        [HarmonyPatch(typeof(Character), nameof(Character.FixedUpdate))]
        private static bool Prefix(Character __instance)
        {
            var data = __instance.GetCachedShipData();
            data.IsCached = false;

            return true;
        }

        private class CachedShipData
        {
            public CachedShipData()
            {
                LastShip = null;
                IsCached = false;
            }

            public Ship LastShip { get; set; }
            public bool IsCached { get; set; }
        }
    }
}