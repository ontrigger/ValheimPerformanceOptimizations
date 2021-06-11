using System;
using System.Runtime.CompilerServices;
using System.Security.Policy;
using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations
{
    public static class TerrainPatches
    {
        private const int TerrainLodLayer = 30;
        
        private static readonly ConditionalWeakTable<Heightmap, Action<Bounds>> Data = 
            new ConditionalWeakTable<Heightmap, Action<Bounds>>();

        /// <summary>
        ///     The large terrain lod is being rendered into the shadowmap for all shadowed lights
        ///     yet doesn't survive the depth test, making it an entirely useless computation
        ///
        ///     This patch adds a new layer to the terrain and forces all lights with a Light* script
        ///     to not render the terrain into their shadowmap regardless of whether they can cast shadows or not
        ///
        ///     Unfortunately, this patch wont do jack shit for lights without these components, and I don't want
        ///     to hook into the location spawn method just to patch them, the location system is slow as shit as is.
        /// </summary>
        
        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        public static class ZNetScene_Awake_Patch
        {
            private static void Postfix(ZNetScene __instance)
            {
                var gameMain = GameObject.Find("_GameMain");
                var terrainLod = gameMain.transform.Find("Terrain_lod");
                terrainLod.gameObject.layer = TerrainLodLayer;

                var directionalLightTransform = gameMain.transform.Find("Directional Light");
                var directionalLight = directionalLightTransform.GetComponent<Light>();
                directionalLight.cullingMask &= ~(1 << TerrainLodLayer);
                
                ValheimPerformanceOptimizations.Logger.LogInfo("YO " + ZoneSystem.instance.m_waterLevel);
            }
        }

        [HarmonyPatch(typeof(LightLod), "Awake")]
        public static class LightLod_Awake_Patch
        {
            private static void Postfix(LightLod __instance, Light ___m_light)
            {
                ___m_light.cullingMask &= ~(1 << TerrainLodLayer);
            }
        }

        [HarmonyPatch(typeof(LightFlicker), "Awake")]
        public static class LightFlicker_Awake_Patch
        {
            private static void Postfix(LightFlicker __instance, Light ___m_light)
            {
                ___m_light.cullingMask &= ~(1 << TerrainLodLayer);
            }
        }
        
        [HarmonyPatch(typeof(Heightmap), "Regenerate")]
        public static class Heightmap_Regenerate_Patch
        {
            private static void Postfix(Heightmap __instance, Bounds ___m_bounds)
            {
                Action<Bounds> action;
                if (Data.TryGetValue(__instance, out action))
                {
                    action.Invoke(___m_bounds);
                }
            }
        }
        
        [HarmonyPatch(typeof(WaterVolume), "Awake")]
        public static class WaterVolume_Awake_Patch
        {
            private static void Postfix(WaterVolume __instance, ref Heightmap ___m_heightmap, BoxCollider ___m_collider)
            {
                if (!___m_heightmap || !___m_collider) return;
                
                Data.Add(___m_heightmap, (heightmapBounds) =>
                {
                    /*var highestLocalPoint = ___m_collider.center + new Vector3(0, ___m_collider.size.y, 0) * 0.5f;
                    var highestWorldSpaceY = ___m_collider.transform.TransformPoint(highestLocalPoint).y;*/

                    var waterLevel = ZoneSystem.m_instance.m_waterLevel + 1f;
                    __instance.m_waterSurface.enabled = waterLevel >= heightmapBounds.min.y;
                });
            }
        }
    }
}