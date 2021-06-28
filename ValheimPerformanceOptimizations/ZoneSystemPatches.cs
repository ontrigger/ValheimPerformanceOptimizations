using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace ValheimPerformanceOptimizations
{
    [HarmonyPatch]
    public static class ZoneSystemPatches
    {
        private static readonly Dictionary<ZoneSystem.ZoneVegetation, GameObjectPool> VegetationPoolByName =
            new Dictionary<ZoneSystem.ZoneVegetation, GameObjectPool>();

        private static readonly Dictionary<GameObject, GameObjectPool> ObjectIdToPool = new Dictionary<GameObject, GameObjectPool>();

        private static GameObject vegetationPoolRoot;

        private static Heightmap zoneHeightmap;

        [HarmonyPatch(typeof(ZoneSystem), "Start")]
        public static void Postfix(ZoneSystem __instance)
        {
            vegetationPoolRoot = new GameObject("VPOVegetationPool");
            vegetationPoolRoot.transform.SetParent(__instance.transform);

            ZNetView.StartGhostInit();

            foreach (var vegetation in __instance.m_vegetation)
            {
                if (!vegetation.m_enable || !vegetation.m_prefab.GetComponent<ZNetView>()) continue;
                
                var toPool = (int) (vegetation.m_max * vegetation.m_groupSizeMax);
                var pool = new GameObjectPool(vegetation.m_prefab, vegetationPoolRoot.transform, toPool, toPool);
                
                foreach (var gameObject in pool.Pool)
                {
                    ZNetView component = gameObject.GetComponent<ZNetView>();
                    if (component && component.GetZDO() != null)
                    {
                        ZDO zDO = component.GetZDO();
                        component.ResetZDO();
                        if (zDO.IsOwner())
                        {
                            ZDOMan.instance.DestroyZDO(zDO);
                        }
                    }
                }
                VegetationPoolByName[vegetation] = pool;
            }
            ZNetView.FinishGhostInit();

            zoneHeightmap = __instance.m_zonePrefab.GetComponentInChildren<Heightmap>();
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
            ObjectIdToPool.Clear();
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
                    var destroyed = 0;
                    foreach (var tempSpawnedObject in __instance.m_tempSpawnedObjects)
                    {
                        if (ObjectIdToPool.TryGetValue(tempSpawnedObject, out var pool))
                        {
                            pool.ReturnObject(tempSpawnedObject, out _);
                        }
                        else
                        {
                            Object.Destroy(tempSpawnedObject);
                        }

                        destroyed += 1;
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
        
        [HarmonyPatch(typeof(ZoneSystem), "PlaceVegetation")]
        public static bool Prefix(
            ZoneSystem __instance, Vector2i zoneID, Vector3 zoneCenterPos, Transform parent, Heightmap hmap,
            List<ZoneSystem.ClearArea> clearAreas, ZoneSystem.SpawnMode mode, List<GameObject> spawnedObjects)
        {
            Profiler.BeginSample("Inside PlaecVeggie");
            var state = Random.state;
            var seed = WorldGenerator.instance.GetSeed();
            var num = __instance.m_zoneSize / 2f;
            var num2 = 1;

            foreach (var item in __instance.m_vegetation)
            {
                num2++;
                if (!item.m_enable || !hmap.HaveBiome(item.m_biome))
                {
                    continue;
                }

                Random.InitState(seed + zoneID.x * 4271 + zoneID.y * 9187 +
                                 item.m_prefab.name.GetStableHashCode());
                var num3 = 1;
                if (item.m_max < 1f)
                {
                    if (Random.value > item.m_max)
                    {
                        continue;
                    }
                }
                else
                {
                    num3 = Random.Range((int) item.m_min, (int) item.m_max + 1);
                }

                if (!VegetationPoolByName.TryGetValue(item, out var objectPool))
                {
                    ValheimPerformanceOptimizations.Logger.LogInfo("Couldnt find key " + item.m_prefab.name);
                }

                var flag = item.m_prefab.GetComponent<ZNetView>() != null;
                var num4 = Mathf.Cos((float) Math.PI / 180f * item.m_maxTilt);
                var num5 = Mathf.Cos((float) Math.PI / 180f * item.m_minTilt);
                var num6 = num - item.m_groupRadius;
                var num7 = item.m_forcePlacement ? num3 * 50 : num3;
                var num8 = 0;
                for (var i = 0; i < num7; i++)
                {
                    var vector =
                        new Vector3(Random.Range(zoneCenterPos.x - num6, zoneCenterPos.x + num6), 0f,
                                    Random.Range(zoneCenterPos.z - num6, zoneCenterPos.z + num6));
                    var num9 = Random.Range(item.m_groupSizeMin, item.m_groupSizeMax + 1);
                    var flag2 = false;
                    for (var j = 0; j < num9; j++)
                    {
                        var p = j == 0 ? vector : __instance.GetRandomPointInRadius(vector, item.m_groupRadius);
                        float num10 = Random.Range(0, 360);
                        var num11 = Random.Range(item.m_scaleMin, item.m_scaleMax);
                        var x = Random.Range(0f - item.m_randTilt, item.m_randTilt);
                        var z = Random.Range(0f - item.m_randTilt, item.m_randTilt);
                        if (item.m_blockCheck && __instance.IsBlocked(p))
                        {
                            continue;
                        }

                        Profiler.BeginSample("Get ground data");
                        __instance.GetGroundData(ref p, out var normal, out var biome, out var biomeArea,
                                                 out var hmap2);
                        Profiler.EndSample();
                        if ((item.m_biome & biome) == 0 || (item.m_biomeArea & biomeArea) == 0)
                        {
                            continue;
                        }

                        var num12 = p.y - __instance.m_waterLevel;
                        if (num12 < item.m_minAltitude || num12 > item.m_maxAltitude)
                        {
                            continue;
                        }

                        if (item.m_minOceanDepth != item.m_maxOceanDepth)
                        {
                            var oceanDepth = hmap2.GetOceanDepth(p);
                            if (oceanDepth < item.m_minOceanDepth || oceanDepth > item.m_maxOceanDepth)
                            {
                                continue;
                            }
                        }

                        if (normal.y < num4 || normal.y > num5)
                        {
                            continue;
                        }

                        if (item.m_terrainDeltaRadius > 0f)
                        {
                            __instance.GetTerrainDelta(p, item.m_terrainDeltaRadius, out var delta, out var _);
                            if (delta > item.m_maxTerrainDelta || delta < item.m_minTerrainDelta)
                            {
                                continue;
                            }
                        }

                        if (item.m_inForest)
                        {
                            var forestFactor = WorldGenerator.GetForestFactor(p);
                            if (forestFactor < item.m_forestTresholdMin || forestFactor > item.m_forestTresholdMax)
                            {
                                continue;
                            }
                        }

                        if (__instance.InsideClearArea(clearAreas, p))
                        {
                            continue;
                        }

                        if (item.m_snapToWater)
                        {
                            p.y = __instance.m_waterLevel;
                        }

                        p.y += item.m_groundOffset;
                        var identity = Quaternion.identity;
                        identity = !(item.m_chanceToUseGroundTilt > 0f) ||
                                   !(Random.value <= item.m_chanceToUseGroundTilt)
                            ? Quaternion.Euler(x, num10, z)
                            : Quaternion.AngleAxis(num10, normal);
                        if (flag)
                        {
                            if (mode == ZoneSystem.SpawnMode.Full || mode == ZoneSystem.SpawnMode.Ghost)
                            {
                                if (mode == ZoneSystem.SpawnMode.Ghost)
                                {
                                    ZNetView.StartGhostInit();
                                }

                                GameObject gameObject;
                                var fromPool = false;
                                if (mode == ZoneSystem.SpawnMode.Ghost && objectPool != null)
                                {
                                    gameObject = objectPool.GetObject(p, identity, out fromPool);
                                    ObjectIdToPool[gameObject] = objectPool;
                                    
                                    var netView = gameObject.GetComponent<ZNetView>();

                                    var zdo = ZDOMan.instance.CreateNewZDO(gameObject.transform.position);
                                    zdo.SetPrefab(item.m_prefab.name.GetStableHashCode());
                                    zdo.SetRotation(identity);
                                    if (netView.m_syncInitialScale)
                                    {
                                        zdo.Set("scale", gameObject.transform.localScale);
                                    }

                                    netView.m_zdo = zdo;
                                }
                                else
                                {
                                    gameObject = Object.Instantiate(item.m_prefab, p, identity);
                                    if (mode == ZoneSystem.SpawnMode.Ghost)
                                    {
                                        ValheimPerformanceOptimizations.Logger.LogInfo(
                                            "No pool? " + item.m_prefab.name);
                                    }
                                }

                                gameObject.name = item.m_prefab.name;

                                /*if (placedCounts.TryGetValue(item.m_prefab.name, out var tuple))
                                {
                                    if (fromPool)
                                    {
                                        tuple.FromPoolCount += 1;
                                    }
                                    else
                                    {
                                        tuple.NewlyCreatedCount += 1;
                                    }

                                    tuple.isFullSpawnMode = mode == ZoneSystem.SpawnMode.Full; 

                                    placedCounts[item.m_prefab.name] = tuple;
                                }
                                else
                                {
                                    placedCounts[item.m_prefab.name] = new CountEvaluation();
                                }*/

                                var component = gameObject.GetComponent<ZNetView>();
                                component.SetLocalScale(new Vector3(num11, num11, num11));
                                component.GetZDO().SetPGWVersion(__instance.m_pgwVersion);

                                if (mode == ZoneSystem.SpawnMode.Ghost)
                                {
                                    spawnedObjects.Add(gameObject);
                                    ZNetView.FinishGhostInit();
                                }
                            }
                        }
                        else
                        {
                            var obj = Object.Instantiate(item.m_prefab, p, identity);
                            obj.transform.localScale = new Vector3(num11, num11, num11);
                            obj.transform.SetParent(parent, true);
                        }

                        flag2 = true;
                    }

                    if (flag2)
                    {
                        num8++;
                    }

                    if (num8 >= num3)
                    {
                        break;
                    }
                }
            }

            /*foreach (var item in placedCounts)
            {
                ValheimPerformanceOptimizations.Logger.LogInfo(
                    $"Placed {item.Key} Unpooled: {item.Value.NewlyCreatedCount} Pooled: {item.Value.FromPoolCount} isFull?: {item.Value.isFullSpawnMode}");
            }*/
            Profiler.EndSample();

            Random.state = state;


            return false;
        }

        #region Profiling

        [HarmonyPatch(typeof(ZNetScene), "RemoveObjects")]
        private static bool Prefix(ZNetScene __instance, List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)
        {
            var frameCount = Time.frameCount;
            foreach (var currentNearObject in currentNearObjects)
            {
                currentNearObject.m_tempRemoveEarmark = frameCount;
            }

            foreach (var currentDistantObject in currentDistantObjects)
            {
                currentDistantObject.m_tempRemoveEarmark = frameCount;
            }

            __instance.m_tempRemoved.Clear();
            foreach (var value in __instance.m_instances.Values)
            {
                if (value.GetZDO().m_tempRemoveEarmark != frameCount)
                {
                    __instance.m_tempRemoved.Add(value);
                }
            }

            for (var i = 0; i < __instance.m_tempRemoved.Count; i++)
            {
                var zNetView = __instance.m_tempRemoved[i];
                var zDO = zNetView.GetZDO();
                zNetView.ResetZDO();

                Object.Destroy(zNetView.gameObject);
                if (!zDO.m_persistent && zDO.IsOwner())
                {
                    ZDOMan.instance.DestroyZDO(zDO);
                }

                __instance.m_instances.Remove(zDO);
            }

            return false;
        }


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