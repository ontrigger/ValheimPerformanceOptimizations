using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.Profiling;
using Object = UnityEngine.Object;

namespace ValheimPerformanceOptimizations.Patches
{
    public static partial class ObjectPoolingPatch
    {
        private static readonly Dictionary<string, Action<ZNetView>> PrefabAwakeProcessors =
            new Dictionary<string, Action<ZNetView>>();

        private static readonly Dictionary<string, Action<ZNetView>> PrefabDestroyProcessors =
            new Dictionary<string, Action<ZNetView>>();

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
        private static ConfigEntry<float> _pooledObjectCountMultiplier;

        public static void Initialize(ConfigFile configFile, Harmony harmony)
        {
            const string keyPooling = "Object pooling enabled";
            const string descriptionPooling =
                "Experimental: if enabled vegetation objects are pulled and pushed from an object pool, rather then creating and destroying them everytime. This greatly increases performance when moving through the world, but can lead to objects spawning at wrong positions or having wrong data. This is an experimental feature, please report any issues that may occur.";
            const string keyCountMultiplier = "Pooled object count multiplier";
            const string descriptionCountMultiplier =
                "Changes how many objects are kept in the pool, increasing the value leads to fewer respawning, but uses more memory. Default value should be fine in most cases.";

            _objectPoolingEnabled = configFile.Bind("Object Pooling", keyPooling, true, descriptionPooling);
            _pooledObjectCountMultiplier =
                configFile.Bind("Object Pooling", keyCountMultiplier, 1f, descriptionCountMultiplier);

            if (_objectPoolingEnabled.Value)
            {
                harmony.PatchAll(typeof(ObjectPoolingPatch));
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
                                                                           acc + (int) (veg.m_max *
                                                                               veg.m_groupSizeMax));
                maxObjectsByVegetation[vegetationForPrefab[0]] = maxPossibleObjects;
            }

            ZNetView.StartGhostInit();

            CreateZoneSystemPool(maxObjectsByVegetation);

            CreateZNetScenePool(maxObjectsByVegetation);
            ZNetView.FinishGhostInit();
        }

        private static void ExtractPrefabProcessors(GameObject prefab)
        {
            Action<ZNetView> objectEnabledProcessor = (zNetView) => { };
            Action<ZNetView> objectDisabledProcessor = (zNetView) => { };
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

            PrefabAwakeProcessors[prefab.name] = objectEnabledProcessor;
            PrefabDestroyProcessors[prefab.name] = objectDisabledProcessor;
        }

        // TODO: move this shit to a separate file
        private static void LodFadeProcessor(ZNetView zNetView)
        {
            zNetView.GetComponentInChildren<LodFadeInOut>().Awake();
        }

        private static void PickableAwakeProcessor(ZNetView zNetView)
        {
            var zDO = zNetView.GetZDO();
            if (zDO == null) return;

            var pickable = zNetView.GetComponentInChildren<Pickable>();

            pickable.m_picked = zDO.GetBool("picked");
            if (pickable.m_picked && pickable.m_hideWhenPicked)
            {
                pickable.m_hideWhenPicked.SetActive(value: false);
            }

            if (pickable.m_respawnTimeMinutes > 0)
            {
                pickable.InvokeRepeating(nameof(Pickable.UpdateRespawn), UnityEngine.Random.Range(1f, 5f), 60f);
            }
        }

        private static void PickableDestroyProcessor(ZNetView zNetView)
        {
            zNetView.GetComponentInChildren<Pickable>().CancelInvoke(nameof(Pickable.UpdateRespawn));
        }

        private static void WearNTearEnabledProcessor(ZNetView zNetView)
        {
            var wearNTear = zNetView.GetComponentInChildren<WearNTear>();

            WearNTear.m_allInstances.Add(wearNTear);
            wearNTear.m_myIndex = WearNTear.m_allInstances.Count - 1;
            wearNTear.m_createTime = Time.time;
            wearNTear.m_support = wearNTear.GetMaxSupport();
            wearNTear.m_piece = wearNTear.GetComponent<Piece>();
            wearNTear.m_colliders = null;
            if (WearNTear.m_randomInitialDamage)
            {
                var value = UnityEngine.Random.Range(0.1f * wearNTear.m_health, wearNTear.m_health * 0.6f);
                zNetView.GetZDO().Set("health", value);
            }

            wearNTear.UpdateVisual(triggerEffects: false);
        }

        private static void WearNTearDisabledProcessor(ZNetView zNetView)
        {
            zNetView.GetComponentInChildren<WearNTear>().OnDestroy();
        }

        private static void PieceEnabledProcessor(ZNetView zNetView)
        {
            var piece = zNetView.GetComponentInChildren<Piece>();

            Piece.m_allPieces.Add(piece);
            piece.m_myListIndex = Piece.m_allPieces.Count - 1;
            piece.m_creator = zNetView.GetZDO().GetLong(Piece.m_creatorHash, 0L);
        }

        private static void PieceDisabledProcessor(ZNetView zNetView)
        {
            zNetView.GetComponentInChildren<Piece>().OnDestroy();
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
                processor(netView);
            }

            Profiler.EndSample();
        }

        private static void OnReturnedToPool(GameObject obj)
        {
            Profiler.BeginSample("POOL REturn");
            var prefabName = Utils.GetPrefabName(obj);

            if (PrefabDestroyProcessors.TryGetValue(prefabName, out var processor))
            {
                processor(obj.GetComponent<ZNetView>());
            }

            Profiler.EndSample();
        }

        [UsedImplicitly]
        public static GameObject GetOrInstantiateObject(
            ZoneSystem.SpawnMode mode, Dictionary<string, GameObjectPool> poolDict,
            GameObject prefab, Vector3 position, Quaternion rotation)
        {
            GameObject gameObject;
            var pool = GetPoolForObject(poolDict, prefab);

            ZNetViewPrefabNamePatch.PrefabNameHack = prefab.name;
            if (mode == ZoneSystem.SpawnMode.Ghost && pool != null)
            {
                gameObject = pool.GetObject(position, rotation);
            }
            else
            {
                ValheimPerformanceOptimizations.Logger.LogInfo("NOT IN POOL? " + prefab.name);
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

        private static GameObjectPool GetPoolForObject(Dictionary<string, GameObjectPool> poolDict, GameObject prefab)
        {
            poolDict.TryGetValue(prefab.name, out var objectPool);

            return objectPool;
        }
    }
}