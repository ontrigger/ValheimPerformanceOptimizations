using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations
{
    /// <summary>
    /// Setting a MeshCollider sharedmesh is expensive because Unity has to bake it into its physics. It can be called
    /// threaded, this is done here. Because the ZoneSystem logic wants to have a collision mesh instantly, it also has
    /// to wait for the baking to complete
    /// </summary>
    [HarmonyPatch]
    public static class ThreadedHeightmapCollisionBakePatch
    {
        private static bool hasGenerationThread;

        // Tuple: heightmap.InstanceID, m_collisionMesh.InstanceID
        private static readonly Queue<Tuple<int, int>> ToBake = new Queue<Tuple<int, int>>();
        private static readonly List<Tuple<int, int>> Ready = new List<Tuple<int, int>>();
        private static readonly Dictionary<int, bool> HeightmapFinished = new Dictionary<int, bool>();
        private static Dictionary<Vector2i, GameObject> spawnedZones = new Dictionary<Vector2i, GameObject>();

        private static void BakeThread()
        {
            while (true)
            {
                Tuple<int, int> next = null;

                lock (ToBake)
                {
                    if (ToBake.Count > 0)
                    {
                        next = ToBake.Dequeue();
                    }
                }

                if (next == null)
                {
                    Thread.Sleep(1);
                    continue;
                }

                // bake the current mesh to be used in a MeshCollider
                Physics.BakeMesh(next.Item2, false);

                lock (Ready)
                {
                    Ready.Add(next);
                }
            }
        }

        [HarmonyPatch(typeof(Heightmap), "Awake"), HarmonyPostfix]
        private static void AwakePatch(Heightmap __instance)
        {
            if (!hasGenerationThread)
            {
                new Thread(BakeThread).Start();
                hasGenerationThread = true;
            }

            if ((bool) __instance.m_collider)
            {
                // cookingOptions has to be default, otherwise no pre-baking is possible
                __instance.m_collider.cookingOptions = MeshColliderCookingOptions.CookForFasterSimulation |
                                                       MeshColliderCookingOptions.EnableMeshCleaning |
                                                       MeshColliderCookingOptions.UseFastMidphase |
                                                       MeshColliderCookingOptions.WeldColocatedVertices;
            }

            HeightmapFinished.Add(__instance.GetInstanceID(), false);
        }

        // check if a mesh is finished backing
        [HarmonyPatch(typeof(Heightmap), "Update"), HarmonyPostfix]
        private static void UpdatePatch(Heightmap __instance)
        {
            int instanceId = __instance.GetInstanceID();

            if ((bool) __instance.m_collider)
            {
                lock (Ready)
                {
                    Tuple<int, int> newMesh = Ready.FirstOrDefault(i => i.Item1 == instanceId);

                    if (newMesh != null)
                    {
                        Ready.Remove(newMesh);

                        __instance.m_collider.sharedMesh = __instance.m_collisionMesh;
                        __instance.m_dirty = true;
                        HeightmapFinished[__instance.GetInstanceID()] = true;
                    }
                }
            }
        }

        // remove line: 'm_collider.sharedMesh = m_collisionMesh;'
        // it must not be called yet, no collision data is baked
        [HarmonyPatch(typeof(Heightmap), "RebuildCollisionMesh"), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> RebuildCollisionMeshTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);

            int foundIndex = -1;
            for (int i = 0; i < code.Count; i++)
            {
                CodeInstruction instruction = code[i];
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
        [HarmonyPatch(typeof(Heightmap), "RebuildCollisionMesh"), HarmonyPostfix]
        private static void RebuildCollisionMeshPatch(Heightmap __instance)
        {
            lock (ToBake)
            {
                ToBake.Enqueue(new Tuple<int, int>(__instance.GetInstanceID(), __instance.m_collisionMesh.GetInstanceID()));
            }
        }

        public static bool IsHeightmapReady(Vector3 pos)
        {
            bool any = false;
            bool ready = true;

            foreach (Heightmap heightmap in Heightmap.m_heightmaps)
            {
                if (heightmap.IsPointInside(pos))
                {
                    any = true;
                    ready = ready && HeightmapFinished[heightmap.GetInstanceID()];
                }
            }

            return any && ready;
        }

        [HarmonyPatch(typeof(Heightmap), "OnDestroy"), HarmonyPostfix]
        private static void OnDestroyPatch(Heightmap __instance)
        {
            if (!ZoneSystem.instance) return;

            Vector2i zonePos = ZoneSystem.instance.GetZone(__instance.transform.position);
            if (spawnedZones.ContainsKey(zonePos))
            {
                spawnedZones.Remove(zonePos);
            }
        }

        [HarmonyPatch(typeof(ClutterSystem), "IsHeightmapReady"), HarmonyPostfix]
        private static void IsHeightmapReadyPatch(ClutterSystem __instance, ref bool __result)
        {
            // only change the result if it was true
            if (!__result) return;

            Camera mainCamera = Utils.GetMainCamera();
            __result = IsHeightmapReady(mainCamera.transform.position);
        }

        // spawn the heightmap GameObject but not call any placement until the heightmap has a collision mesh
        [HarmonyPatch(typeof(ZoneSystem), "SpawnZone"), HarmonyPrefix]
        private static bool SpawnZone(ZoneSystem __instance, ref bool __result, Vector2i zoneID, ZoneSystem.SpawnMode mode, out GameObject root)
        {
            Vector3 zonePos = __instance.GetZonePos(zoneID);
            if (!spawnedZones.ContainsKey(zoneID))
            {
                Heightmap componentInChildren = __instance.m_zonePrefab.GetComponentInChildren<Heightmap>();
                if (!HeightmapBuilder.instance.IsTerrainReady(zonePos, componentInChildren.m_width, componentInChildren.m_scale, componentInChildren.m_isDistantLod, WorldGenerator.instance))
                {
                    root = null;
                    __result = false;
                    return false;
                }

                root = UnityEngine.Object.Instantiate(__instance.m_zonePrefab, zonePos, Quaternion.identity);
                spawnedZones.Add(zoneID, root);
            }
            else
            {
                root = spawnedZones[zoneID];
            }

            if ((mode == ZoneSystem.SpawnMode.Ghost || mode == ZoneSystem.SpawnMode.Full) && !__instance.IsZoneGenerated(zoneID))
            {
                int heightmapInstanceId = root.GetComponentInChildren<Heightmap>().GetInstanceID();

                if (!HeightmapFinished[heightmapInstanceId])
                {
                    __result = false;
                    return false;
                }

                Heightmap componentInChildren2 = root.GetComponentInChildren<Heightmap>();
                __instance.m_tempClearAreas.Clear();
                __instance.m_tempSpawnedObjects.Clear();
                __instance.PlaceLocations(zoneID, zonePos, root.transform, componentInChildren2, __instance.m_tempClearAreas, mode, __instance.m_tempSpawnedObjects);
                __instance.PlaceVegetation(zoneID, zonePos, root.transform, componentInChildren2, __instance.m_tempClearAreas, mode, __instance.m_tempSpawnedObjects);
                __instance.PlaceZoneCtrl(zoneID, zonePos, mode, __instance.m_tempSpawnedObjects);
                if (mode == ZoneSystem.SpawnMode.Ghost)
                {
                    foreach (GameObject tempSpawnedObject in __instance.m_tempSpawnedObjects)
                    {
                        UnityEngine.Object.Destroy(tempSpawnedObject);
                    }

                    __instance.m_tempSpawnedObjects.Clear();
                    UnityEngine.Object.Destroy(root);
                    root = null;
                }

                __instance.SetZoneGenerated(zoneID);
            }

            __result = true;
            return false;
        }
    }
}
