using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches
{
    /// <summary>
    /// Remove a pointless Color[32x32] allocation just to clear the paintmask
    /// </summary>
    [HarmonyPatch]
    public class HeightmapColorAllocationPatch
    {
        private static Color[] _clearColors;

        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.Awake))]
        public static void Postfix(Heightmap __instance)
        {
            if (_clearColors == null && !__instance.m_isDistantLod)
            {
                _clearColors = new Color[__instance.m_width * __instance.m_width];
            }
        }

        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.Generate))]
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
            __instance.m_paintMask.SetPixels(__instance.m_isDistantLod
                                                 ? new Color[__instance.m_width * __instance.m_width]
                                                 : _clearColors);
            __instance.ApplyModifiers();

            return false;
        }
    }
}