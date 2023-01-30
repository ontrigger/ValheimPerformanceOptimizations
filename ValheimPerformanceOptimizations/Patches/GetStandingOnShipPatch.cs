using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;

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
            if (!ModCompatibility.IsValheimRaftPresent)
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
            
            if (__instance.m_nview.IsValid())
            {
	            float fixedDeltaTime = Time.fixedDeltaTime;
	            __instance.UpdateLayer();
	            Profiler.BeginSample("cont effect");
	            __instance.UpdateContinousEffects();
	            Profiler.EndSample();
	            Profiler.BeginSample("upd waer");
	            __instance.UpdateWater(fixedDeltaTime);
	            __instance.UpdateGroundTilt(fixedDeltaTime);
	            __instance.SetVisible(__instance.m_nview.HasOwner());
	            Profiler.EndSample();
	            __instance.UpdateLookTransition(fixedDeltaTime);
	            if (__instance.m_nview.IsOwner())
	            {
		            __instance.UpdateGroundContact(fixedDeltaTime);
		            Profiler.BeginSample("noiz");
		            __instance.UpdateNoise(fixedDeltaTime);
		            Profiler.EndSample();
		            Profiler.BeginSample("seman");
		            __instance.m_seman.Update(fixedDeltaTime);
		            Profiler.EndSample();
		            __instance.UpdateStagger(fixedDeltaTime);
		            __instance.UpdatePushback(fixedDeltaTime);
		            Profiler.BeginSample("motion");
		            __instance.UpdateMotion(fixedDeltaTime);
		            Profiler.EndSample();
		            Profiler.BeginSample("smoke");
		            __instance.UpdateSmoke(fixedDeltaTime);
		            Profiler.EndSample();
		            __instance.UnderWorldCheck(fixedDeltaTime);
		            __instance.SyncVelocity();
		            __instance.CheckDeath();
	            }
            }

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