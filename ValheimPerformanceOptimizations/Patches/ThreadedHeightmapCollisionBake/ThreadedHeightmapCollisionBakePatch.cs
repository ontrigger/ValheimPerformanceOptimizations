using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches
{
    /// <summary>
    ///     Setting a MeshCollider sharedmesh is expensive because Unity has to bake it into its physics. It can be called
    ///     threaded, this is done here. Because the ZoneSystem logic wants to have a collision mesh instantly, it also has
    ///     to wait for the baking to complete
    /// </summary>
    public static class ThreadedHeightmapCollisionBakePatch
    {
        public static readonly Dictionary<Heightmap, bool> HeightmapFinished = new Dictionary<Heightmap, bool>();
        private static readonly Dictionary<Vector2i, GameObject> SpawnedZones = new Dictionary<Vector2i, GameObject>();

        private static ConfigEntry<bool> _threadedCollisionBakeEnabled;

        static ThreadedHeightmapCollisionBakePatch()
        {
            ValheimPerformanceOptimizations.OnInitialized += Initialize;
        }

        public static void Initialize(ConfigFile configFile, Harmony harmony)
        {
            const string key = "Threaded terrain collision baking enabled";
            const string description =
                "Experimental: if enabled terrain is generated in parallel, this reduces lag spikes when moving through the world. This is an experimental feature, please report any issues that may occur.";
            _threadedCollisionBakeEnabled = configFile.Bind("General", key, true, description);

            if (_threadedCollisionBakeEnabled.Value)
            {
                harmony.PatchAll(typeof(ThreadedHeightmapCollisionBakePatch));
            }
        }

        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.OnEnable)), HarmonyPostfix]
        private static void OnEnablePatch(Heightmap __instance)
        {
            if (!__instance.m_isDistantLod || !Application.isPlaying || __instance.m_distantLodEditorHax)
            {
                VPOTerrainCollisionBaker.Instance.RequestCollisionBake(__instance, true);
            }
        }

        private static bool IsHeightmapReady(Vector3 pos)
        {
            var any = false;
            var ready = true;

            foreach (var heightmap in Heightmap.m_heightmaps)
            {
                if (heightmap.IsPointInside(pos))
                {
                    any = true;
                    ready = ready && HeightmapFinished[heightmap];
                }
            }

            return any && ready;
        }

        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.Awake)), HarmonyPostfix]
        private static void AwakePatch(Heightmap __instance)
        {
            if (__instance.m_collider)
            {
                // cookingOptions has to be default, otherwise no pre-baking is possible
                __instance.m_collider.cookingOptions = MeshColliderCookingOptions.CookForFasterSimulation |
                                                       MeshColliderCookingOptions.EnableMeshCleaning |
                                                       MeshColliderCookingOptions.UseFastMidphase |
                                                       MeshColliderCookingOptions.WeldColocatedVertices;
            }

            HeightmapFinished.Add(__instance, false);
        }

        // remove line: 'm_collider.sharedMesh = m_collisionMesh;'
        // it must not be called yet, no collision data is baked
        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.RebuildCollisionMesh)), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> RebuildCollisionMeshTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            var foundIndex = -1;
            for (var i = 0; i < code.Count; i++)
            {
                var instruction = code[i];
                if (instruction.ToString().Contains("set_sharedMesh"))
                {
                    foundIndex = i;
                }
            }

            if (foundIndex > -1)
            {
                code.RemoveRange(foundIndex - 4, 5);
            }

            return code.AsEnumerable();
        }

        // enqueue current collision mesh to be baked in the separate thread
        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.RebuildCollisionMesh)), HarmonyPostfix]
        private static void RebuildCollisionMeshPatch(Heightmap __instance)
        {
            if (__instance.m_collider)
            {
                VPOTerrainCollisionBaker.Instance.RequestCollisionBake(__instance);
            }
        }

        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.OnDestroy)), HarmonyPostfix]
        private static void OnDestroyPatch(Heightmap __instance)
        {
            if (!ZoneSystem.instance) return;

            var zonePos = ZoneSystem.instance.GetZone(__instance.transform.position);
            if (SpawnedZones.ContainsKey(zonePos))
            {
                SpawnedZones.Remove(zonePos);
            }
        }

        [HarmonyPatch(typeof(ClutterSystem), nameof(ClutterSystem.IsHeightmapReady)), HarmonyPostfix]
        private static void IsHeightmapReadyPatch(ClutterSystem __instance, ref bool __result)
        {
            // only change the result if it was true
            if (!__result) return;

            var mainCamera = Utils.GetMainCamera();
            __result = IsHeightmapReady(mainCamera.transform.position);
        }

        // spawn the heightmap GameObject but not call any placement until the heightmap has a collision mesh
        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.SpawnZone)), HarmonyPrefix]
        private static bool SpawnZone(
            ZoneSystem __instance, ref bool __result, Vector2i zoneID, ZoneSystem.SpawnMode mode, out GameObject root)
        {
            var zonePos = __instance.GetZonePos(zoneID);
            
            var componentInChildren = __instance.m_zonePrefab.GetComponentInChildren<Heightmap>();
            if (!HeightmapBuilder.instance.IsTerrainReady(zonePos, componentInChildren.m_width,
                                                          componentInChildren.m_scale,
                                                          componentInChildren.m_isDistantLod,
                                                          WorldGenerator.instance))
            {
                root = null;
                __result = false;
                return false;
            }

            root = GetOrCreateZone(__instance.m_zonePrefab, zoneID, zonePos);

            var heightmap = root.GetComponentInChildren<Heightmap>();
            if (!HeightmapFinished[heightmap])
            {
                __result = false;
                return false;
            }

            if ((mode == ZoneSystem.SpawnMode.Ghost || mode == ZoneSystem.SpawnMode.Full) &&
                !__instance.IsZoneGenerated(zoneID))
            {
                __instance.m_tempClearAreas.Clear();
                __instance.m_tempSpawnedObjects.Clear();
                __instance.PlaceLocations(zoneID, zonePos, root.transform, heightmap, __instance.m_tempClearAreas, mode,
                                          __instance.m_tempSpawnedObjects);
                __instance.PlaceVegetation(zoneID, zonePos, root.transform, heightmap, __instance.m_tempClearAreas,
                                           mode, __instance.m_tempSpawnedObjects);
                __instance.PlaceZoneCtrl(zoneID, zonePos, mode, __instance.m_tempSpawnedObjects);
                if (mode == ZoneSystem.SpawnMode.Ghost)
                {
                    foreach (var tempSpawnedObject in __instance.m_tempSpawnedObjects)
                    {
                        ObjectPoolingPatch.DestroyOrReturnPooledObject(ObjectPoolingPatch.VegetationPoolByName, tempSpawnedObject);
                    }

                    __instance.m_tempSpawnedObjects.Clear();
                    Object.Destroy(root);
                    root = null;
                }

                __instance.SetZoneGenerated(zoneID);
            }

            __result = true;
            return false;
        }

        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Shutdown)), HarmonyPostfix]
        public static void ZNetScene_Shutdown_Postfix(ZNetScene __instance)
        {
            SpawnedZones.Clear();
            HeightmapFinished.Clear();
        }

        private static GameObject GetOrCreateZone(GameObject zonePrefab, Vector2i zoneID, Vector3 zonePos)
        {
            GameObject zone;
            if (!SpawnedZones.ContainsKey(zoneID))
            {
                zone = Object.Instantiate(zonePrefab, zonePos, Quaternion.identity);
                SpawnedZones.Add(zoneID, zone);
            }
            else
            {
                zone = SpawnedZones[zoneID];
            }

            return zone;
        }
    }
}
