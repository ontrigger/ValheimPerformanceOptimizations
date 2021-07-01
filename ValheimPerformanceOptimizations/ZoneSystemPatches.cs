using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.Profiling;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace ValheimPerformanceOptimizations
{
    [HarmonyPatch]
    public static class ZoneSystemPatches
    {
        public static Dictionary<string, GameObjectPool> VegetationPoolByName;

        public static Heightmap zoneHeightmap;

        private static readonly HashSet<string> PrefabsWithFadeComponent = new HashSet<string>();

        private static readonly MethodInfo GetNetViewComponentMethod =
            AccessTools.GetDeclaredMethods(typeof(GameObject))
                       .Where(m => m.Name == "GetComponent" && m.GetGenericArguments().Length == 1)
                       .Select(m => m.MakeGenericMethod(typeof(ZNetView)))
                       .First();
        
        private static readonly MethodInfo ObjectInstantiateMethod =
            AccessTools.GetDeclaredMethods(typeof(Object))
                       .Where(m => m.Name == "Instantiate" && m.GetGenericArguments().Length == 1)
                       .Select(m => m.MakeGenericMethod(typeof(GameObject)))
                       .First(m =>
                                  m.GetParameters().Length == 3 &&
                                  m.GetParameters()[1].ParameterType == typeof(Vector3));

        private static readonly MethodInfo GetPoolForObjectMethod =
            AccessTools.DeclaredMethod(typeof(ZoneSystemPatches), "GetPoolForObject");
        
        private static readonly MethodInfo GetOrInstantiateObjectMethod =
            AccessTools.DeclaredMethod(typeof(ZoneSystemPatches), "GetOrInstantiateObject");

        [HarmonyPatch(typeof(ZoneSystem), "Start")]
        public static void Postfix(ZoneSystem __instance)
        {
            ZNetView.StartGhostInit();

            // @formatter:off
            VegetationPoolByName = __instance.m_vegetation
                .GroupBy(veg => veg.m_prefab.name)
                .Select(group =>
                {
                    var vegetationForName = group.ToList();
                    var prefab = vegetationForName[0].m_prefab;
                    var toPool = vegetationForName.Aggregate(0, (acc, veg) =>
                                                                 acc + (int) (veg.m_max * veg.m_groupSizeMax));

                    var pool = new GameObjectPool(prefab, toPool, OnRetrievedFromPool);

                    if (prefab.GetComponentInChildren<LodFadeInOut>())
                    {
                        PrefabsWithFadeComponent.Add(prefab.name);
                    }

                    pool.Populate(toPool, obj =>
                    {
                        var component = obj.GetComponent<ZNetView>();
                        if (component && component.GetZDO() != null)
                        {
                            var zDO = component.GetZDO();
                            component.ResetZDO();
                            if (zDO.IsOwner())
                            {
                                ZDOMan.instance.DestroyZDO(zDO);
                            }
                        }
                    });

                    return new Tuple<string, GameObjectPool>(prefab.name, pool);
                }).ToDictionary(tuple => tuple.Item1, tuple => tuple.Item2);
            // @formatter:on

            ZNetView.FinishGhostInit();

            zoneHeightmap = __instance.m_zonePrefab.GetComponentInChildren<Heightmap>();
        }

        private static void OnRetrievedFromPool(GameObject obj)
        {
            var netView = obj.GetComponent<ZNetView>();
            netView.Awake();

            if (PrefabsWithFadeComponent.Contains(obj.name))
            {
                // some prefabs have their lod fade on the second level
                obj.GetComponentInChildren<LodFadeInOut>().Awake();
            }
        }

        [UsedImplicitly]
        private static GameObject GetOrInstantiateObject(
            ZoneSystem.SpawnMode mode, GameObject prefab, Vector3 position, Quaternion rotation)
        {
            GameObject gameObject;
            var pool = GetPoolForObject(prefab);
            if (mode == ZoneSystem.SpawnMode.Ghost && pool != null)
            {
                gameObject = pool.GetObject(position, rotation);
            }
            else
            {
                gameObject = Object.Instantiate(prefab, position, rotation);
            }

            gameObject.name = prefab.name;

            return gameObject;
        }

        [UsedImplicitly]
        private static GameObjectPool GetPoolForObject(GameObject prefab)
        {
            VegetationPoolByName.TryGetValue(prefab.name, out var objectPool);
            
            return objectPool;
        }
        
        [HarmonyPatch(typeof(ZoneSystem), "OnDestroy"), HarmonyPostfix]
        public static void ZoneSystem_OnDestroy_Postfix(ZoneSystem __instance)
        {
            VegetationPoolByName.Values.ToList().ForEach(pool => pool.Destroy());
        }

        [HarmonyPatch(typeof(ZoneSystem), "SpawnZone")]
        public static bool Prefix(
            ZoneSystem __instance, Vector2i zoneID, ZoneSystem.SpawnMode mode, out GameObject root, ref bool __result)
        {
            Profiler.BeginSample("Spawn zone");
            var zonePos = __instance.GetZonePos(zoneID);
            if (!HeightmapBuilder.instance.IsTerrainReady(zonePos, zoneHeightmap.m_width, zoneHeightmap.m_scale,
                                                          zoneHeightmap.m_isDistantLod, WorldGenerator.instance))
            {
                root = null;
                __result = false;

                return false;
            }

            root = Object.Instantiate(__instance.m_zonePrefab, zonePos, Quaternion.identity);
            if ((mode == ZoneSystem.SpawnMode.Ghost || mode == ZoneSystem.SpawnMode.Full) &&
                !__instance.IsZoneGenerated(zoneID))
            {
                var componentInChildren2 = root.GetComponentInChildren<Heightmap>();

                __instance.m_tempClearAreas.Clear();
                __instance.m_tempSpawnedObjects.Clear();
                Profiler.BeginSample("PlaceLocations");
                __instance.PlaceLocations(zoneID, zonePos, root.transform, componentInChildren2,
                                          __instance.m_tempClearAreas, mode, __instance.m_tempSpawnedObjects);
                Profiler.EndSample();

                Profiler.BeginSample("PlaceVegetation");
                __instance.PlaceVegetation(zoneID, zonePos, root.transform, componentInChildren2,
                                           __instance.m_tempClearAreas, mode, __instance.m_tempSpawnedObjects);
                Profiler.EndSample();
                Profiler.BeginSample("PlaceZoneCtrl");
                __instance.PlaceZoneCtrl(zoneID, zonePos, mode, __instance.m_tempSpawnedObjects);
                Profiler.EndSample();

                if (mode == ZoneSystem.SpawnMode.Ghost)
                {
                    foreach (var tempSpawnedObject in __instance.m_tempSpawnedObjects)
                    {
                        if (VegetationPoolByName.TryGetValue(tempSpawnedObject.name, out var pool))
                        {
                            pool.ReturnObject(tempSpawnedObject);
                        }
                        else
                        {
                            Object.Destroy(tempSpawnedObject);
                        }
                    }

                    __instance.m_tempSpawnedObjects.Clear();
                    Object.Destroy(root);

                    root = null;
                }

                __instance.SetZoneGenerated(zoneID);
            }

            Profiler.EndSample();

            __result = true;
            return false;
        }

        [HarmonyPatch(typeof(ZoneSystem), "PlaceVegetation"), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var code = new List<CodeInstruction>(instructions);
            
            // in order to declare a local i have to increment all other locals
            // after mine by 1. I also have to make the ilgenerator create a local with the correct index.
            // so, for now the object pool lookup has to be done in GetOrInstantiateObject instead of 
            // at the beginning of the PlaceVegetation method :(

            /*var foundIndex = -1;
            for (var i = 0; i < code.Count; i++)
            {
                var instruction = code[i];

                if (instruction.Is(OpCodes.Callvirt, GetNetViewComponentMethod))
                {
                    foundIndex = i - 2;
                    break;
                }
            }
            
            var objectPoolLocal = generator.DeclareLocal(typeof(GameObjectPool));
            code.InsertRange(foundIndex, new[]
            {
                new CodeInstruction(OpCodes.Ldloc_S, 5), // item
                new CodeInstruction(OpCodes.Call, GetPoolForObjectMethod),
                new CodeInstruction(OpCodes.Stloc_S, objectPoolLocal)
            });*/

            var instantiationIndex = -1;
            for (var i = 0; i < code.Count; i++)
            {
                var instruction = code[i];

                if (instruction.Is(OpCodes.Call, ObjectInstantiateMethod))
                {
                    instantiationIndex = i - 4;
                    break;
                }
            }
            
            // add the mode before the arguments
            code.InsertRange(instantiationIndex, new []
            {
                new CodeInstruction(OpCodes.Ldarg_S, 6), // mode
                //new CodeInstruction(OpCodes.Ldloc_S, objectPoolLocal)
            });
            
            // replace the call to instantiate with our method
            code[instantiationIndex + 1 + 4] = new CodeInstruction(OpCodes.Call, GetOrInstantiateObjectMethod);
            
            return code.AsEnumerable();
        }

        #region Profiling

        [HarmonyPatch(typeof(ZoneSystem), "Update")]
        public static bool Prefix(ZoneSystem __instance)
        {
            Profiler.BeginSample("ZOne system awake");
            if (ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.Connected)
            {
                return false;
            }

            __instance.m_updateTimer += Time.deltaTime;
            if (!(__instance.m_updateTimer > 0.1f))
            {
                return false;
            }

            __instance.m_updateTimer = 0f;
            var flag = __instance.CreateLocalZones(ZNet.instance.GetReferencePosition());
            __instance.UpdateTTL(0.1f);
            if (!ZNet.instance.IsServer() || flag)
            {
                return false;
            }

            __instance.CreateGhostZones(ZNet.instance.GetReferencePosition());
            foreach (var peer in ZNet.instance.GetPeers())
            {
                __instance.CreateGhostZones(peer.GetRefPos());
            }

            Profiler.EndSample();

            return false;
        }

        [HarmonyPatch(typeof(ZoneSystem), "CreateGhostZones")]
        public static bool Prefix(ZoneSystem __instance, Vector3 refPoint, ref bool __result)
        {
            Profiler.BeginSample("CreateGhostZones");
            var zone = __instance.GetZone(refPoint);
            if (!__instance.IsZoneGenerated(zone) && __instance.SpawnZone(zone, ZoneSystem.SpawnMode.Ghost, out var _))
            {
                __result = true;
                return false;
            }

            var num = __instance.m_activeArea + __instance.m_activeDistantArea;
            for (var i = zone.y - num; i <= zone.y + num; i++)
            {
                for (var j = zone.x - num; j <= zone.x + num; j++)
                {
                    var zoneID = new Vector2i(j, i);
                    if (!__instance.IsZoneGenerated(zoneID) &&
                        __instance.SpawnZone(zoneID, ZoneSystem.SpawnMode.Ghost, out var _))
                    {
                        __result = true;
                        return false;
                    }
                }
            }

            Profiler.EndSample();
            __result = false;
            return false;
        }

        #endregion
    }
}