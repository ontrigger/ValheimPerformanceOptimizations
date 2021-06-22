using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations
{
    /// <summary>
    ///     The large terrain lod is being rendered into the shadowmap for all shadowed lights
    ///     yet doesn't survive the depth test, making it an entirely useless computation
    ///     This patch adds a new layer to the terrain and forces all lights with a Light* script
    ///     to not render the terrain into their shadowmap regardless of whether they can cast shadows or not
    ///     Unfortunately, this patch wont do jack shit for lights without these components, and I don't want
    ///     to hook into the location spawn method just to patch them, the location system is slow as shit as is.
    /// </summary>
    [HarmonyPatch]
    public static class LargeTerrainShadowPatch
    {
        private const int TerrainLodLayer = 30;

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static void Postfix(ZNetScene __instance)
        {
            var gameMain = GameObject.Find("_GameMain");
            var terrainLod = gameMain.transform.Find("Terrain_lod");
            terrainLod.gameObject.layer = TerrainLodLayer;

            // don't render it into the directional shadows either, nobody will notice
            var directionalLightTransform = gameMain.transform.Find("Directional Light");
            var directionalLight = directionalLightTransform.GetComponent<Light>();
            directionalLight.cullingMask &= ~(1 << TerrainLodLayer);

            //gameMain.AddComponent<VPOSmokeRenderer>();
        }

        [HarmonyPatch(typeof(LightLod), "Awake")]
        private static void Postfix(LightLod __instance, Light ___m_light)
        {
            ___m_light.cullingMask &= ~(1 << TerrainLodLayer);
        }

        [HarmonyPatch(typeof(LightFlicker), "Awake")]
        private static void Postfix(LightFlicker __instance, Light ___m_light)
        {
            ___m_light.cullingMask &= ~(1 << TerrainLodLayer);
        }
    }
}