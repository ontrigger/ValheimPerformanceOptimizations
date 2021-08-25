using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches
{
    /// <summary>
    ///     The water planes are being rendered even when they are fully below the terrain
    ///     This patch makes water render only if the lowest point of its terrain intersects with the water level
    /// </summary>
    [HarmonyPatch]
    public static class WaterVolumeVisibilityPatch
    {
        private static readonly ConditionalWeakTable<Heightmap, Action<Bounds>> HeightmapChangedCallbacks =
            new ConditionalWeakTable<Heightmap, Action<Bounds>>();

        [HarmonyPatch(typeof(Heightmap),  nameof(Heightmap.Regenerate))]
        private static void Postfix(Heightmap __instance, Bounds ___m_bounds)
        {
            Action<Bounds> action;
            if (HeightmapChangedCallbacks.TryGetValue(__instance, out action))
            {
                action.Invoke(___m_bounds);
            }
        }

        [HarmonyPatch(typeof(WaterVolume), nameof(WaterVolume.Awake))]
        private static void Postfix(WaterVolume __instance)
        {
            if (!__instance.m_heightmap || !__instance.m_collider)
            {
                return;
            }

            HeightmapChangedCallbacks.Add(__instance.m_heightmap, heightmapBounds =>
            {
                var waterLevel = ZoneSystem.m_instance.m_waterLevel + 1f;
                __instance.m_waterSurface.enabled = waterLevel >= heightmapBounds.min.y;
            });
        }
    }
}