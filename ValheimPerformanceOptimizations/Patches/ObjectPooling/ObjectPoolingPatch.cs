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
            AccessTools.Method(typeof(Object), "Destroy", new[] {typeof(Object)});

        private static readonly MethodInfo GetOrInstantiateObjectMethod =
            AccessTools.DeclaredMethod(typeof(ObjectPoolingPatch), "GetOrInstantiateObject");

        private static readonly MethodInfo DestroyOrReturnObjectMethod =
            AccessTools.DeclaredMethod(typeof(ObjectPoolingPatch), "DestroyOrReturnPooledObject");

        private static ConfigEntry<bool> _objectPoolingEnabled;
        private static ConfigEntry<int> _pooledObjectCount;

        public static void Initialize(ConfigFile configFile, Harmony harmony)
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

        [HarmonyPatch(typeof(ZoneSystem), "Start")]
        public static void Postfix(ZoneSystem __instance)
        {
            var maxObjectsByVegetation = new Dictionary<ZoneSystem.ZoneVegetation, int>();
            foreach (var vegetationGroup in __instance.m_vegetation.GroupBy(veg => veg.m_prefab.name))
            {
                var vegetationForPrefab = vegetationGroup.ToList();
                var prefab = vegetationForPrefab[0].m_prefab;

                ExtractPrefabProcessors(prefab);

                var maxPossibleObjects = vegetationForPrefab.Aggregate(0, (acc, veg) =>
                                                                           acc + (int) (veg.m_max * veg.m_groupSizeMax));
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

            PrefabAwakeProcessors[prefab.name] = objectEnabledProcessor;
            PrefabDestroyProcessors[prefab.name] = objectDisabledProcessor;
        }

        // TODO: move this shit to a separate file
        private static void LodFadeProcessor(ComponentCache componentCache)
        {
            componentCache.GetComponent<LodFadeInOut>().Awake();
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
            var wearNTear = componentCache.WearNTear;

            WearNTear.m_allInstances.Add(wearNTear);
            wearNTear.m_myIndex = WearNTear.m_allInstances.Count - 1;
            wearNTear.m_createTime = Time.time;
            wearNTear.m_support = wearNTear.GetMaxSupport();
            wearNTear.m_piece = wearNTear.GetComponent<Piece>();
            wearNTear.m_colliders = null;
            if (WearNTear.m_randomInitialDamage)
            {
                var value = Random.Range(0.1f * wearNTear.m_health, wearNTear.m_health * 0.6f);
                componentCache.NetView.GetZDO().Set("health", value);
            }

            wearNTear.UpdateVisual(false);
        }

        private static void WearNTearDisabledProcessor(ComponentCache componentCache)
        {
            componentCache.WearNTear.OnDestroy();
        }

        private static void PieceEnabledProcessor(ComponentCache componentCache)
        {
            var piece = componentCache.Piece;

            Piece.m_allPieces.Add(piece);
            piece.m_myListIndex = Piece.m_allPieces.Count - 1;
            piece.m_creator = componentCache.NetView.GetZDO().GetLong(Piece.m_creatorHash);
        }

        private static void PieceDisabledProcessor(ComponentCache componentCache)
        {
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

        [HarmonyPatch(typeof(ZoneSystem), "OnDestroy"), HarmonyPostfix]
        private static void ZoneSystem_OnDestroy_Postfix(ZoneSystem __instance)
        {
            DestroyZoneSystemPool();
            DestroyZNetScenePool();
        }

        private static void OnRetrievedFromPool(GameObject obj)
        {
            Profiler.BeginSample("POOL RETRIEVE");
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

            Profiler.EndSample();
        }

        private static void OnReturnedToPool(GameObject obj)
        {
            var prefabName = Utils.GetPrefabName(obj);

            if (PrefabDestroyProcessors.TryGetValue(prefabName, out var processor))
            {
                if (!ComponentCacheForObject.TryGetValue(obj, out var componentCache))
                {
                    componentCache = new ComponentCache(obj.GetComponentInChildren<ZNetView>());
                    ComponentCacheForObject.Add(obj, componentCache);
                }

                processor(componentCache);
            }
        }

        [UsedImplicitly]
        public static GameObject GetOrInstantiateObject(
            ZoneSystem.SpawnMode mode,
            Dictionary<string, GameObjectPool> poolDict,
            GameObject prefab,
            Vector3 position,
            Quaternion rotation)
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

        public static void DestroyOrReturnPooledObject(Dictionary<string, GameObjectPool> poolDict, GameObject tempSpawnedObject)
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

        public Pickable Pickable => _pickable == null ? _pickable = NetView.GetComponentInChildren<Pickable>() : _pickable;

        public WearNTear WearNTear => _wearNTear == null ? _wearNTear = NetView.GetComponentInChildren<WearNTear>() : _wearNTear;

        public T GetComponent<T>()
        {
            return NetView.GetComponent<T>();
        }

        private Pickable _pickable;

        private Piece _piece;

        private TerrainModifier _terrainModifier;

        private WearNTear _wearNTear;
    }
}