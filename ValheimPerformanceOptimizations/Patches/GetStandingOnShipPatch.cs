using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;

namespace ValheimPerformanceOptimizations.Patches
{
    public static class GetStandingOnShipPatch
    {
        private static readonly Dictionary<Character, Ship> LastShipByCharacter = new();

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

        [HarmonyPatch(typeof(Character), nameof(Character.OnDestroy)), HarmonyPostfix]
        private static void OnDestroy_Postfix(Character __instance)
        {
	        LastShipByCharacter.Remove(__instance);
        }

        [HarmonyPatch(typeof(Character), nameof(Character.GetStandingOnShip)), HarmonyPrefix]
        private static bool Prefix(Character __instance, ref Ship __result)
        {
	        if (!__instance.IsOnGround())
	        {
		        __result = null;
		        return false;
	        } 
	       
	        if (__instance.m_lastGroundBody)
	        {
		        LastShipByCharacter.TryGetValue(__instance, out var ship);
		        __result = ship;
		        return false;
	        }
	        
	        __result = null;
	        return false;
        }

        [HarmonyPatch(typeof(Character), nameof(Character.UpdateGroundContact)), HarmonyPostfix]
        private static void UpdateGroundContact_Postfix(Character __instance, float dt)
        {
	        if (__instance.m_lastGroundBody != null)
	        {
		        LastShipByCharacter[__instance] = __instance.m_lastGroundBody.GetComponent<Ship>();
	        }
	        else
	        {
		        LastShipByCharacter[__instance] = null;
	        }
        }

        #if DEBUG
        [HarmonyPatch(typeof(Character), nameof(Character.FixedUpdate))]
        private static bool Prefix(Character __instance)
        {
            if (__instance.m_nview.IsValid())
            {
	            Profiler.BeginSample("All");

	            float fixedDeltaTime = Time.fixedDeltaTime;
	            Profiler.BeginSample("layer");
	            __instance.UpdateLayer();
	            Profiler.EndSample();
	            Profiler.BeginSample("cont effect");
	            __instance.UpdateContinousEffects();
	            Profiler.EndSample();
	            Profiler.BeginSample("upd waer");
	            __instance.UpdateWater(fixedDeltaTime);
	            __instance.UpdateGroundTilt(fixedDeltaTime);
	            __instance.SetVisible(__instance.m_nview.HasOwner());
	            Profiler.EndSample();
	            Profiler.BeginSample("look trans");
	            __instance.UpdateLookTransition(fixedDeltaTime);
	            Profiler.EndSample();
	            if (__instance.m_nview.IsOwner())
	            {
		            Profiler.BeginSample("ground contact");
		            __instance.UpdateGroundContact(fixedDeltaTime);
		            Profiler.EndSample();
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
		            Profiler.BeginSample("rest");
		            __instance.UnderWorldCheck(fixedDeltaTime);
		            __instance.SyncVelocity();
		            __instance.CheckDeath();
		            Profiler.EndSample();
	            }
	            Profiler.EndSample();
            }

            return false;
        }
        #endif
    }
}