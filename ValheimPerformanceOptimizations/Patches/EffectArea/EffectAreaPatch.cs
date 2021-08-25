using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches
{
    [HarmonyPatch]
    public static class EffectAreaPatch
    {
        [HarmonyPatch(typeof(EffectArea), nameof(EffectArea.Awake))]
        private static bool Prefix(EffectArea __instance)
        {
            var patchedEffectArea = __instance.gameObject.AddComponent<VPOEffectArea>();
            patchedEffectArea.m_statusEffect = __instance.m_statusEffect;
            patchedEffectArea.m_type = __instance.m_type;

            Object.Destroy(__instance);

            return false;
        }
    }
}