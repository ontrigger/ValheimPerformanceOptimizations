using System;
using System.Collections.Generic;
using HarmonyLib;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Profiling;

namespace ValheimPerformanceOptimizations.Patches
{
    /// <summary>
    /// Remove a pointless Color[32x32] allocation just to clear the paintmask 
    /// </summary>
    [HarmonyPatch]
    public class HeightmapColorAllocationPatch
    {
        private static Color[] _clearColors;

        [HarmonyPatch(typeof(Heightmap), "Awake")]
        public static void Postfix(Heightmap __instance)
        {
            if (_clearColors == null && !__instance.m_isDistantLod)
            {
                _clearColors = new Color[__instance.m_width * __instance.m_width];
            }
        }

        [HarmonyPatch(typeof(Heightmap), "Generate")]
        public static bool Prefix(Heightmap __instance)
        {
            __instance.Initialize();
            var num = __instance.m_width + 1;
            var num2 = num * num;
            var position = __instance.transform.position;
            if (__instance.m_buildData == null || __instance.m_buildData.m_baseHeights.Count != num2 ||
                __instance.m_buildData.m_center != position || __instance.m_buildData.m_scale != __instance.m_scale ||
                __instance.m_buildData.m_worldGen != WorldGenerator.instance)
            {
                __instance.m_buildData = HeightmapBuilder.instance.RequestTerrainSync(
                    position, __instance.m_width, __instance.m_scale, __instance.m_isDistantLod,
                    WorldGenerator.instance);
                __instance.m_cornerBiomes = __instance.m_buildData.m_cornerBiomes;
            }

            for (var i = 0; i < num2; i++)
            {
                __instance.m_heights[i] = __instance.m_buildData.m_baseHeights[i];
            }

            // the only change
            __instance.m_paintMask.SetPixels(__instance.m_isDistantLod ?
                                                 new Color[__instance.m_width * __instance.m_width]
                                                 : _clearColors);
            __instance.ApplyModifiers();

            return false;
        }

        #region Profiling

        [HarmonyPatch(typeof(Heightmap), "Regenerate"), HarmonyPrefix]
        private static bool RegeneratePatch(Heightmap __instance)
        {
            Profiler.BeginSample("Heitmap regen");
            if (__instance.HaveQueuedRebuild())
            {
                __instance.CancelInvoke("Regenerate");
            }
            __instance.Generate();
            Profiler.BeginSample("Rebuild collision");
            __instance.RebuildCollisionMesh();
            Profiler.EndSample();
            __instance.UpdateCornerDepths();
            __instance.m_dirty = true;
            Profiler.EndSample();

            return false;
        }
        
        [HarmonyPatch(typeof(Heightmap), "Poke"), HarmonyPrefix]
        private static bool PokePatch(Heightmap __instance, bool delayed)
        {
            Profiler.BeginSample("Poke maps");
            if (delayed)
            {
                if (__instance.HaveQueuedRebuild())
                {
                    __instance.CancelInvoke("Regenerate");
                }
                __instance.InvokeRepeating("Regenerate", 0.1f, 0f);
            }
            else
            {
                __instance.Regenerate();
            }
            Profiler.EndSample();

            return false;
        }

        #endregion
    }
}