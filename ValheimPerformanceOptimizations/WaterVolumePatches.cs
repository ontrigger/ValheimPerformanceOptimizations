using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations
{
    /// <summary>
    ///     The original GetWaterLevel method used OverlapSphere to check if a point
    ///     intersected a WaterVolume bounds
    ///     This patch skips this expensive check by caching the WaterVolume for each zone
    ///     and getting it via simple Vector2i dict lookup
    ///     This is probably the most massive CPU speedup due to this method being used in 15 places
    /// </summary>
    [HarmonyPatch]
    public static class GetWaterLevelPatch
    {
        private static bool _isPatched;

        [HarmonyPatch(typeof(ZoneSystem), "Awake")]
        private static void Postfix(ZoneSystem __instance)
        {
            if (_isPatched) return;

            __instance.m_zonePrefab.AddComponent<VPOZoneTracker>();
            _isPatched = true;
        }

        [HarmonyPatch(typeof(WaterVolume), "GetWaterLevel")]
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
    }

    /// <summary>
    ///     The water planes are being rendered even when they are fully below the terrain
    ///     This patch makes water render only if the lowest point of its terrain intersects with the water level
    /// </summary>
    [HarmonyPatch]
    public static class WaterVolumeVisibilityPatch
    {
        private static readonly ConditionalWeakTable<Heightmap, Action<Bounds>> HeightmapChangedCallbacks =
            new ConditionalWeakTable<Heightmap, Action<Bounds>>();

        [HarmonyPatch(typeof(Heightmap), "Regenerate")]
        private static void Postfix(Heightmap __instance, Bounds ___m_bounds)
        {
            Action<Bounds> action;
            if (HeightmapChangedCallbacks.TryGetValue(__instance, out action))
            {
                action.Invoke(___m_bounds);
            }
        }

        [HarmonyPatch(typeof(WaterVolume), "Awake")]
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

    /// <summary>
    ///     Cache components for each zone
    /// </summary>
    public class VPOZoneTracker : MonoBehaviour
    {
        private static readonly Dictionary<Vector2i, CachedZoneComponents> ZoneComponentsByLocation =
            new Dictionary<Vector2i, CachedZoneComponents>();

        public Vector2i zonePosition;

        private void Awake()
        {
            zonePosition = ZoneSystem.instance.GetZone(transform.position);

            var cachedData = new CachedZoneComponents
            {
                Heightmap = GetComponentInChildren<Heightmap>(),
                WaterVolume = GetComponentInChildren<WaterVolume>()
            };

            ZoneComponentsByLocation.Add(zonePosition, cachedData);
        }

        private void OnDestroy()
        {
            ZoneComponentsByLocation.Remove(zonePosition);
        }

        public static bool GetZoneComponents(Vector3 worldPos, out CachedZoneComponents components)
        {
            var zonePosition = ZoneSystem.instance.GetZone(worldPos);

            return ZoneComponentsByLocation.TryGetValue(zonePosition, out components);
        }

        public struct CachedZoneComponents
        {
            public Heightmap Heightmap;
            public WaterVolume WaterVolume;

            public CachedZoneComponents(Heightmap heightmap, WaterVolume waterVolume)
            {
                Heightmap = heightmap;
                WaterVolume = waterVolume;
            }
        }
    }
}