using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ValheimPerformanceOptimizations.Patches
{
    public static class ZoneSystemObjectPoolingPatch
    {
        public static Dictionary<string, GameObjectPool> VegetationPoolByName;

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

        private static readonly MethodInfo ObjectDestroyMethod =
            AccessTools.Method(typeof(Object), "Destroy", new[] {typeof(Object)});

        private static readonly MethodInfo GetPoolForObjectMethod =
            AccessTools.DeclaredMethod(typeof(ZoneSystemObjectPoolingPatch), "GetPoolForObject");

        private static readonly MethodInfo GetOrInstantiateObjectMethod =
            AccessTools.DeclaredMethod(typeof(ZoneSystemObjectPoolingPatch), "GetOrInstantiateObject");

        private static readonly MethodInfo DestroyOrReturnObjectMethod =
            AccessTools.DeclaredMethod(typeof(ZoneSystemObjectPoolingPatch), "DestroyOrReturnPooledObject");

        private static ConfigEntry<bool> _objectPoolingEnabled;
        private static ConfigEntry<float> _pooledObjectCountMultiplier;

        public static void Initialize(ConfigFile configFile, Harmony harmony)
        {
            _objectPoolingEnabled = configFile.Bind("Object Pooling", "Object pooling enabled", true);
            _pooledObjectCountMultiplier = configFile.Bind("Object Pooling", "Pooled object count multiplier", 1f);
            if (_objectPoolingEnabled.Value)
            {
                harmony.PatchAll(typeof(ZoneSystemObjectPoolingPatch));
            }
        }

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
                            acc + (int) (veg.m_max * veg.m_groupSizeMax * _pooledObjectCountMultiplier.Value));

                    ValheimPerformanceOptimizations.Logger.LogInfo($"Total pooled objects {toPool} {prefab.name}");
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
        }

        #region ZoneSystemPooling

        [HarmonyPatch(typeof(ZoneSystem), "OnDestroy"), HarmonyPostfix]
        private static void ZoneSystem_OnDestroy_Postfix(ZoneSystem __instance)
        {
            VegetationPoolByName.Values.ToList().ForEach(pool => pool.Destroy());
        }

        [HarmonyPatch(typeof(ZoneSystem), "PlaceVegetation"), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
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
            code.InsertRange(instantiationIndex, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_S, 6), // mode
                //new CodeInstruction(OpCodes.Ldloc_S, objectPoolLocal)
            });

            // replace the call to instantiate with our method
            code[instantiationIndex + 1 + 4] = new CodeInstruction(OpCodes.Call, GetOrInstantiateObjectMethod);

            return code.AsEnumerable();
        }

        [HarmonyPatch(typeof(ZoneSystem), "SpawnZone"), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> ZoneSystem_SpawnZone_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            var destroyCallIndex = code.FindIndex(instruction => instruction.Is(OpCodes.Call, ObjectDestroyMethod));
            code[destroyCallIndex] = new CodeInstruction(OpCodes.Call, DestroyOrReturnObjectMethod);

            return code.AsEnumerable();
        }

        #endregion

        #region ZNetScenePooling

        // replace destroy call with pool return
        [HarmonyPatch(typeof(ZNetScene), "RemoveObjects"), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> ZNetScene_RemoveObjects_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            var destroyCallIndex = code.FindIndex(instruction => instruction.Is(OpCodes.Call, ObjectDestroyMethod));
            code[destroyCallIndex] = new CodeInstruction(OpCodes.Call, DestroyOrReturnObjectMethod);

            return code.AsEnumerable();
        }

        // replace instantiation with pool get
        [HarmonyPatch(typeof(ZNetScene), "CreateObject"), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> ZNetScene_CreateObject_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            var instantiateCallIndex =
                code.FindIndex(instruction => instruction.Is(OpCodes.Call, ObjectInstantiateMethod));
            // prepend the spawnmode to args
            code.Insert(instantiateCallIndex - 3, new CodeInstruction(OpCodes.Ldc_I4_2));
            code[instantiateCallIndex + 1] = new CodeInstruction(OpCodes.Call, GetOrInstantiateObjectMethod);

            return code.AsEnumerable();
        }

        #endregion

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
        public static GameObject GetOrInstantiateObject(
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

        public static void DestroyOrReturnPooledObject(GameObject tempSpawnedObject)
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

        [UsedImplicitly]
        private static GameObjectPool GetPoolForObject(GameObject prefab)
        {
            VegetationPoolByName.TryGetValue(prefab.name, out var objectPool);

            return objectPool;
        }
    }
}