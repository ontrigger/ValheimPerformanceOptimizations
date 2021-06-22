using HarmonyLib;
using UnityEngine.Rendering;

namespace ValheimPerformanceOptimizations
{
    [HarmonyPatch]
    public class ClutterSystemPatches
    {
        [HarmonyPatch(typeof(ClutterSystem), "Awake")]
        private static void Postfix(ClutterSystem __instance)
        {
            foreach (var clutter in __instance.m_clutter)
            {
                var renderer = clutter.m_prefab.GetComponent<InstanceRenderer>();
                if (!renderer) { continue; }

                renderer.m_material.renderQueue = (int) RenderQueue.AlphaTest;
            }
        }
    }
}