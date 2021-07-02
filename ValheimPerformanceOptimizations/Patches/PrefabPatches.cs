using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches
{
    [HarmonyPatch]
    public static class PrefabPatches
    {
        private const float LeafParticleCullHeight = 0.79f;

        private static readonly List<string> PrefabsWithLeafParticles = new List<string>
        {
            "Beech1", "Birch1_aut", "Birch2_aut", "Oak1"
        };

        private static readonly List<string> MaterialsWithDisabledInstancing = new List<string>
        {
            "straw_roof", "straw_roof_worn", "Grill_mat", "meadbase", "poison",
            "poisonres_potion", "stamina_potion"
        }.Select(material => material + " (Instance)").ToList();

        private static readonly List<string> PrefabsWithDisabledInstancing = new List<string>
        {
            "OLD_wood_roof_icorner", "wood_roof_icorner_45", "wood_roof_ocorner", "wood_roof_icorner",
            "OLD_wood_roof_ocorner", "wood_roof_45", "wood_roof_top_45", "wood_roof_ocorner_45",
            "wood_roof", "wood_roof_top", "OLD_wood_roof", "piece_cookingstation", "MeadBaseFrostResist",
            "MeadBaseHealthMedium", "MeadBaseHealthMinor", "MeadBasePoisonResist", "MeadBaseStaminaMedium",
            "MeadBaseStaminaMinor", "MeadBaseTasty", "MeadPoisonResist", "MeadStaminaMedium", "MeadStaminaMinor"
        };

        private static bool _isPatched;

        /// <summary>
        ///     Some trees have leaf particles attached to them.
        ///     These particles get rendered at insane distances
        ///     where they have a screen size of a pixel at 1080p, while also not being instanced.
        ///     This patch inserts a new lod that will cull particles at a reasonable screen height.
        ///     It also makes the particles instanced, but it doesn't seem to instance more than 5
        ///     quads at a time for some reason
        /// </summary>
        private static void PatchPrefabWithLeaves(GameObject prefab)
        {
            var leafTransform = Utils.FindChild(prefab.transform, "leaf_particles");

            var ps = leafTransform.GetComponent<ParticleSystem>();
            var psRenderer = ps.GetComponent<ParticleSystemRenderer>();
            psRenderer.renderMode = ParticleSystemRenderMode.Mesh;
            psRenderer.enableGPUInstancing = true;

            var lodGroup = prefab.GetComponent<LODGroup>();
            if (!lodGroup)
            {
                return;
            }

            var lods = lodGroup.GetLODs();
            var lod0 = lods[0];

            var prevHeight = lod0.screenRelativeTransitionHeight;
            lod0.screenRelativeTransitionHeight = LeafParticleCullHeight;

            var noParticleRenderers = lod0.renderers
                                          .Where(r => r.name != "leaf_particles")
                                          .ToArray();

            var newLods = new LOD[lods.Length + 1];

            newLods[0] = lod0;
            newLods[1] = new LOD(prevHeight, noParticleRenderers);
            for (var index = 1; index < lods.Length; index++)
            {
                newLods[index + 1] = lods[index];
            }

            lodGroup.SetLODs(newLods);
        }

        private static void PatchPrefabWithUninstancedMaterials(GameObject prefab)
        {
            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                var materials = renderer.materials;
                foreach (var material in materials)
                {
                    if (!MaterialsWithDisabledInstancing.Contains(material.name))
                    {
                        continue;
                    }

                    material.enableInstancing = true;
                }
            }
        }

        private static int PatchPrefabs(
            this IReadOnlyDictionary<int, GameObject> allPrefabs, IEnumerable<string> prefabNames,
            Action<GameObject> patcher)
        {
            var totalPatched = 0;
            foreach (var prefabName in prefabNames)
            {
                if (allPrefabs.TryGetValue(prefabName.GetStableHashCode(), out var prefab))
                {
                    patcher(prefab);
                    totalPatched += 1;
                }
            }

            return totalPatched;
        }


        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static void Postfix(ZNetScene __instance, Dictionary<int, GameObject> ___m_namedPrefabs)
        {
            if (_isPatched) return;

            var namedPrefabs = __instance.m_namedPrefabs;

            var patched = 0;
            patched += namedPrefabs.PatchPrefabs(PrefabsWithLeafParticles, PatchPrefabWithLeaves);
            patched += namedPrefabs.PatchPrefabs(PrefabsWithDisabledInstancing, PatchPrefabWithUninstancedMaterials);

            ValheimPerformanceOptimizations.Logger.LogInfo($"Patched {patched} prefabs");

            _isPatched = true;
        }
    }
}