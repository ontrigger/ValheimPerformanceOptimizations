using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches.GetWaterLevel
{
    /// <summary>
    ///     The original GetWaterLevel method used OverlapSphere to check if a point
    ///     intersected a WaterVolume bounds
    ///     This patch skips this expensive check by caching the WaterVolume for each zone
    ///     and getting it via simple Vector2i dict lookup
    ///     This is probably the most massive CPU speedup due to this method being used in 15 places
    /// </summary>
    /*[HarmonyPatch]
    public static class GetWaterLevelPatch
    {
        private static bool _isPatched;

        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.Awake))]
        private static void Postfix(ZoneSystem __instance)
        {
            if (_isPatched) return;

            __instance.m_zonePrefab.AddComponent<VPOZoneTracker>();
            _isPatched = true;
        }

        [HarmonyPatch(typeof(WaterVolume), nameof(WaterVolume.GetWaterLevel))]
        private static bool Prefix(WaterVolume __instance, Vector3 p, ref float __result, float waveFactor = 1f)
        {
            if (WaterVolume.m_waterVolumeMask == 0)
            {
                WaterVolume.m_waterVolumeMask = LayerMask.GetMask("WaterVolume");
            }

            var hasZone = VPOZoneTracker.GetZoneComponents(p, out var components);
            if (hasZone)
            {
                var waterVolume = components.WaterVolume;
                if (waterVolume.m_collider.bounds.Contains(p))
                {
                    __result = components.WaterVolume.GetWaterSurface(p, waveFactor);
                    return false;
                }

                __result = -10000f;
                return false;
            }

            // fallback in case the zone wasn't found somehow
            var hitCount =
                Physics.OverlapSphereNonAlloc(p, 0f, WaterVolume.tempColliderArray, WaterVolume.m_waterVolumeMask);
            for (var i = 0; i < hitCount; i++)
            {
                var waterVolume = WaterVolume.tempColliderArray[i].GetComponent<WaterVolume>();
                if (waterVolume)
                {
                    __result = waterVolume.GetWaterSurface(p, waveFactor);
                    return false;
                }
            }

            __result = -10000f;
            return false;
        }
    }*/
}