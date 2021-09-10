using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.Profiling;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace ValheimPerformanceOptimizations.Patches
{
    public static partial class ObjectPoolingPatch
    {
        private static readonly Dictionary<string, Action<ComponentCache>> PrefabAwakeProcessors =
            new Dictionary<string, Action<ComponentCache>>();

        private static readonly Dictionary<string, Action<ComponentCache>> PrefabDestroyProcessors =
            new Dictionary<string, Action<ComponentCache>>();

        private static readonly ConditionalWeakTable<GameObject, ComponentCache> ComponentCacheForObject
            = new ConditionalWeakTable<GameObject, ComponentCache>();

        private static readonly MethodInfo ObjectInstantiateMethod =
            AccessTools.GetDeclaredMethods(typeof(Object))
                       .Where(m => m.Name == "Instantiate" && m.GetGenericArguments().Length == 1)
                       .Select(m => m.MakeGenericMethod(typeof(GameObject)))
                       .First(m =>
                                  m.GetParameters().Length == 3 &&
                                  m.GetParameters()[1].ParameterType == typeof(Vector3));

        private static readonly MethodInfo ObjectDestroyMethod =
            AccessTools.Method(typeof(Object), "Destroy", new[] { typeof(Object) });

        private static readonly MethodInfo GetOrInstantiateObjectMethod =
            AccessTools.DeclaredMethod(typeof(ObjectPoolingPatch), "GetOrInstantiateObject");

        private static readonly MethodInfo DestroyOrReturnObjectMethod =
            AccessTools.DeclaredMethod(typeof(ObjectPoolingPatch), "DestroyOrReturnPooledObject");

        private static ConfigEntry<bool> _objectPoolingEnabled;
        private static ConfigEntry<int> _pooledObjectCount;

        static ObjectPoolingPatch()
        {
            ValheimPerformanceOptimizations.OnInitialized += Initialize;
        }

        private static void Initialize(ConfigFile configFile, Harmony harmony)
        {
            const string keyPooling = "Object pooling enabled";
            const string descriptionPooling =
                "Experimental: if enabled vegetation objects are pulled and pushed from an object pool, rather then creating and destroying them everytime. This greatly increases performance when moving through the world, but can lead to objects spawning at wrong positions or having wrong data. This is an experimental feature, please report any issues that may occur.";
            const string maxPooledObjectsCount = "Max pool size per object";
            const string maxPooledObjectsDescription =
                "Controls the maximum amount of each unique object in the pool. Increasing this value leads to less lag when loading player-created buildings, but uses more memory. Default value should be fine if you have < 9000 instances";

            _objectPoolingEnabled = configFile.Bind("Object Pooling", keyPooling, true, descriptionPooling);
            _pooledObjectCount =
                configFile.Bind("Object Pooling", maxPooledObjectsCount, 500, maxPooledObjectsDescription);

            if (_objectPoolingEnabled.Value)
            {
                harmony.PatchAll(typeof(ObjectPoolingPatch));
                harmony.PatchAll(typeof(ConnectPanelPatch));
            }
        }

        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.Start))]
        public static void Postfix(ZoneSystem __instance)
        {
            var maxObjectsByVegetation = new Dictionary<ZoneSystem.ZoneVegetation, int>();
            foreach (var vegetationGroup in __instance.m_vegetation.GroupBy(veg => veg.m_prefab.name))
            {
                var vegetationForPrefab = vegetationGroup.ToList();
                var prefab = vegetationForPrefab[0].m_prefab;

                // skip prefabs with multiple mods for now
                if (prefab.GetComponentsInChildren<TerrainModifier>().Length > 1) { continue; }

                // skip viewless prefabs
                if (prefab.GetComponentInChildren<ZNetView>() == null) { continue; }

                ExtractPrefabProcessors(prefab);

                var maxPossibleObjects = vegetationForPrefab.Aggregate(0, (acc, veg) =>
                                                                           acc + (int)(veg.m_max * veg.m_groupSizeMax));
                maxObjectsByVegetation[vegetationForPrefab[0]] = maxPossibleObjects;
            }

            ZNetView.StartGhostInit();

            CreateZoneSystemPool(maxObjectsByVegetation);
            CreateZNetScenePool(maxObjectsByVegetation);
            ZNetView.FinishGhostInit();
        }

        private static void ExtractPrefabProcessors(GameObject prefab)
        {
            Action<ComponentCache> objectEnabledProcessor = zNetView => { };
            Action<ComponentCache> objectDisabledProcessor = zNetView => { };
            if (prefab.GetComponentInChildren<LodFadeInOut>())
            {
                objectEnabledProcessor += LodFadeProcessor;
            }

            if (prefab.GetComponentInChildren<Pickable>())
            {
                objectEnabledProcessor += PickableAwakeProcessor;
                objectDisabledProcessor += PickableDestroyProcessor;
            }

            if (prefab.GetComponentInChildren<WearNTear>())
            {
                objectEnabledProcessor += WearNTearEnabledProcessor;
                objectDisabledProcessor += WearNTearDisabledProcessor;
            }

            if (prefab.GetComponentInChildren<Piece>())
            {
                objectEnabledProcessor += PieceEnabledProcessor;
                objectDisabledProcessor += PieceDisabledProcessor;
            }

            if (prefab.GetComponentInChildren<TerrainModifier>())
            {
                objectEnabledProcessor += TerrainModifierEnabledProcessor;
                objectDisabledProcessor += TerrainModifierDisabledProcessor;
            }

            if (prefab.GetComponentInChildren<Destructible>())
            {
                objectEnabledProcessor += DestructibleEnabledProcessor;
                objectDisabledProcessor += DestructibleDisabledProcessor;
            }

            PrefabAwakeProcessors[prefab.name] = objectEnabledProcessor;
            PrefabDestroyProcessors[prefab.name] = objectDisabledProcessor;
        }

        // TODO: move this shit to a separate file
        private static void LodFadeProcessor(ComponentCache componentCache)
        {
            componentCache.GetComponentInChildren<LodFadeInOut>().Awake();
        }

        private static void PickableAwakeProcessor(ComponentCache componentCache)
        {
            var zNetView = componentCache.NetView;

            var zDO = zNetView.GetZDO();
            if (zDO == null) return;

            var pickable = componentCache.Pickable;

            pickable.m_picked = zDO.GetBool("picked");
            if (pickable.m_picked && pickable.m_hideWhenPicked)
            {
                pickable.m_hideWhenPicked.SetActive(false);
            }

            if (pickable.m_respawnTimeMinutes > 0)
            {
                pickable.InvokeRepeating(nameof(Pickable.UpdateRespawn), Random.Range(1f, 5f), 60f);
            }
        }

        private static void PickableDestroyProcessor(ComponentCache componentCache)
        {
            componentCache.Pickable.CancelInvoke(nameof(Pickable.UpdateRespawn));
        }

        private static void WearNTearEnabledProcessor(ComponentCache componentCache)
        {
            if (componentCache.NetView.GetZDO() == null) { return; }

            componentCache.WearNTear.Awake();
        }

        private static void WearNTearDisabledProcessor(ComponentCache componentCache)
        {
            componentCache.WearNTear.OnDestroy();
            
            var zNetView = componentCache.NetView;
            zNetView.Unregister("WNTRemove");
            zNetView.Unregister("WNTDamage");
            zNetView.Unregister("WNTRepair");
            zNetView.Unregister("WNTHealthChanged");
            zNetView.Unregister("WNTCreateFragments");
            
            componentCache.WearNTear.m_colliders = null;
        }

        private static void PieceEnabledProcessor(ComponentCache componentCache)
        {
            var piece = componentCache.Piece;
            if (piece == null) { return; }

            Piece.m_allPieces.Add(piece);
            piece.m_myListIndex = Piece.m_allPieces.Count - 1;
            if (piece.m_nview && piece.m_nview.IsValid())
            {
                piece.m_creator = componentCache.NetView.GetZDO().GetLong(Piece.m_creatorHash);
            }
        }

        private static void PieceDisabledProcessor(ComponentCache componentCache)
        {
            // RuneMagic destroys the piece component on some rocks causing the unchecked code to crash
            if (componentCache.Piece == null) { return; }

            componentCache.Piece.OnDestroy();
        }

        private static void TerrainModifierEnabledProcessor(ComponentCache componentCache)
        {
            componentCache.TerrainModifier.Awake();
        }

        private static void TerrainModifierDisabledProcessor(ComponentCache componentCache)
        {
            componentCache.TerrainModifier.OnDestroy();
        }

        private static void DestructibleEnabledProcessor(ComponentCache componentCache)
        {
            var destructible = componentCache.GetComponentInChildren<Destructible>();
            destructible.m_nview = componentCache.NetView;
            destructible.m_body = destructible.GetComponent<Rigidbody>();

            if (destructible.m_nview && destructible.m_nview.GetZDO() != null)
            {
                if (destructible.m_ttl > 0f)
                {
                    destructible.InvokeRepeating("DestroyNow", destructible.m_ttl, 1f);
                }
            }
        }

        private static void DestructibleDisabledProcessor(ComponentCache componentCache)
        {
            var destructible = componentCache.GetComponentInChildren<Destructible>();
            destructible.CancelInvoke(nameof(Destructible.DestroyNow));
        }

        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Shutdown)), HarmonyPrefix]
        private static void ZNetScene_OnDestroy_Prefix(ZNetScene __instance)
        {
            ReleaseZoneSystemPool();
            ReleaseZNetScenePool();
        }

        private static void OnRetrievedFromPool(GameObject obj)
        {
            var netView = obj.GetComponent<ZNetView>();
            netView.Awake();

            var prefabName = ZNetViewPrefabNamePatch.PrefabNameHack ?? obj.name;

            if (PrefabAwakeProcessors.TryGetValue(prefabName, out var processor))
            {
                if (!ComponentCacheForObject.TryGetValue(obj, out var componentCache))
                {
                    componentCache = new ComponentCache(netView);
                    ComponentCacheForObject.Add(obj, componentCache);
                }

                processor(componentCache);
            }
        }

        private static void OnReturnedToPool(GameObject obj)
        {
            var prefabName = obj.name;

            if (PrefabDestroyProcessors.TryGetValue(prefabName, out var processor))
            {
                Profiler.BeginSample("component cache");
                if (!ComponentCacheForObject.TryGetValue(obj, out var componentCache))
                {
                    componentCache = new ComponentCache(obj.GetComponentInChildren<ZNetView>());
                    ComponentCacheForObject.Add(obj, componentCache);
                }
                Profiler.EndSample();

                Profiler.BeginSample("running processors");
                processor(componentCache);
                Profiler.EndSample();
            }
        }

        [UsedImplicitly]
        public static GameObject GetOrInstantiateObject(
            ZoneSystem.SpawnMode mode, Dictionary<string, GameObjectPool> poolDict,
            GameObject prefab, Vector3 position, Quaternion rotation)
        {
            GameObject gameObject;

            poolDict.TryGetValue(prefab.name, out var pool);

            ZNetViewPrefabNamePatch.PrefabNameHack = prefab.name;
            if (mode == ZoneSystem.SpawnMode.Ghost && pool != null)
            {
                gameObject = pool.GetObject(position, rotation);
            }
            else
            {
                gameObject = Object.Instantiate(prefab, position, rotation);
            }

            ZNetViewPrefabNamePatch.PrefabNameHack = null;

            gameObject.name = prefab.name;

            return gameObject;
        }

        public static void DestroyOrReturnPooledObject(
            Dictionary<string, GameObjectPool> poolDict, GameObject tempSpawnedObject)
        {
            if (poolDict.TryGetValue(tempSpawnedObject.name, out var pool))
            {
                pool.ReturnObject(tempSpawnedObject);
            }
            else
            {
                Object.Destroy(tempSpawnedObject);
            }
        }
    }

    internal class ComponentCache
    {
        public ZNetView NetView;

        public ComponentCache(ZNetView netView)
        {
            NetView = netView;
        }

        public Piece Piece => _piece == null ? _piece = NetView.GetComponentInChildren<Piece>() : _piece;

        public TerrainModifier TerrainModifier => _terrainModifier == null
            ? _terrainModifier = NetView.GetComponentInChildren<TerrainModifier>()
            : _terrainModifier;

        public Pickable Pickable =>
            _pickable == null ? _pickable = NetView.GetComponentInChildren<Pickable>() : _pickable;

        public WearNTear WearNTear =>
            _wearNTear == null ? _wearNTear = NetView.GetComponentInChildren<WearNTear>() : _wearNTear;

        public T GetComponent<T>()
        {
            return NetView.GetComponent<T>();
        }

        public T GetComponentInChildren<T>()
        {
            return NetView.GetComponentInChildren<T>();
        }

        private Pickable _pickable;

        private Piece _piece;

        private TerrainModifier _terrainModifier;

        private WearNTear _wearNTear;
    }
}