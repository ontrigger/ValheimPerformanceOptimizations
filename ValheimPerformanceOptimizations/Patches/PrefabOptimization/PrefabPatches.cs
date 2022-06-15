using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

using VPO = ValheimPerformanceOptimizations.ValheimPerformanceOptimizations;

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
			"Grill_mat", "meadbase", "poison",
			"poisonres_potion", "stamina_potion", "portal_small"
        }.Select(material => material + " (Instance)").ToList();

        private static readonly List<string> PrefabsWithDisabledInstancing = new List<string>
        {
			"piece_cookingstation", "MeadBaseFrostResist", "MeadBaseHealthMedium", 
			"MeadBaseHealthMinor", "MeadBasePoisonResist", "MeadBaseStaminaMedium",
            "MeadBaseStaminaMinor", "MeadBaseTasty", "MeadPoisonResist", "MeadStaminaMedium", 
			"MeadStaminaMinor", "portal_wood"
        };

        private static readonly List<string> PrefabsWithWastedMaterials = new List<string>
        {
            "OLD_wood_roof_icorner", "wood_roof_icorner_45", "wood_roof_ocorner", "wood_roof_icorner",
            "OLD_wood_roof_ocorner", "wood_roof_45", "wood_roof_top_45", "wood_roof_ocorner_45",
            "wood_roof", "wood_roof_top", "OLD_wood_roof"
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

					if (material.enableInstancing)
					{
						VPO.Logger.LogInfo($"material {material.name} is already instanced {prefab.name}");
					}
                    material.enableInstancing = true;
                }
            }
        }

        private static void PatchPrefabWithWastedMaterials(GameObject prefab)
        {
            PrefabMaterialCombiner.CombinePrefabMaterials(prefab);
        }

        private static void PatchSnowStormParticle()
        {
            var environment = GameObject.Find("_GameMain/environment");
            
            var luxLitShader = Shader.Find("Lux Lit Particles/ Bumped");
            
            var snowStormParticles = Utils.FindChild(environment.transform, "SnowStorm");
            var snow = snowStormParticles.GetChild(1).gameObject;

            var snowPs = snow.GetComponent<ParticleSystem>();
            var snowPsMain = snowPs.main;
            snowPsMain.maxParticles = 9000;

            var minMaxCurve = snowPsMain.startSize;
            minMaxCurve.mode = ParticleSystemCurveMode.TwoConstants;
            minMaxCurve.constantMin = 0.03f;
            minMaxCurve.constantMax = 0.07f;
            snowPsMain.startSize = minMaxCurve;

            var snowPsEmission = snowPs.emission;
            var rateOverTime = snowPsEmission.rateOverTime;
            rateOverTime.constant = 3000f;
            snowPsEmission.rateOverTime = rateOverTime;

            var snowPsShape = snowPs.shape;
            snowPsShape.radiusThickness = 1f;

            var snowPsRenderer = snow.GetComponent<ParticleSystemRenderer>();
            var snowClusterMaterial = snowPsRenderer.material;
            snowClusterMaterial.mainTexture = null;
            snowClusterMaterial.DisableKeyword("GEOM_TYPE_FROND");
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


        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
        private static void Postfix(ZNetScene __instance, Dictionary<int, GameObject> ___m_namedPrefabs)
        {
            if (_isPatched) return;

            var namedPrefabs = __instance.m_namedPrefabs;

            var patched = 0;
            patched += namedPrefabs.PatchPrefabs(PrefabsWithLeafParticles, PatchPrefabWithLeaves);
            patched += namedPrefabs.PatchPrefabs(PrefabsWithDisabledInstancing, PatchPrefabWithUninstancedMaterials);
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
            {
                var now = DateTime.Now;
                patched += namedPrefabs.PatchPrefabs(PrefabsWithWastedMaterials, PatchPrefabWithWastedMaterials);
                ValheimPerformanceOptimizations.Logger.LogInfo("Combined prefab mats in " + (DateTime.Now - now).TotalMilliseconds + " ms");
            }

            PatchSnowStormParticle();
            patched += 1;

            ValheimPerformanceOptimizations.Logger.LogInfo($"Patched {patched} prefabs");

			_isPatched = true;
        }
    }
}