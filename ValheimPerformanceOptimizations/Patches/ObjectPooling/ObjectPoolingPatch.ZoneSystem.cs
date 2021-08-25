using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches
{
    public static partial class ObjectPoolingPatch
    {
        public static readonly Dictionary<string, GameObjectPool>
            VegetationPoolByName = new Dictionary<string, GameObjectPool>();

        private static readonly FieldInfo VegetationPoolByNameField =
            AccessTools.DeclaredField(typeof(ObjectPoolingPatch), nameof(VegetationPoolByName));
        
        private static readonly MethodInfo GetOrInstantiatePersistentObjectMethod =
            AccessTools.DeclaredMethod(typeof(ObjectPoolingPatch), nameof(GetOrInstantiatePersistentObject));

        private static void CreateZoneSystemPool(Dictionary<ZoneSystem.ZoneVegetation, int> maxObjectsByVegetation)
        {
            maxObjectsByVegetation.ToList().ForEach(pair =>
            {
                var pool = new GameObjectPool(pair.Key.m_prefab, pair.Value, OnRetrievedFromPool);

                VegetationPoolByName[pair.Key.m_prefab.name] = pool;
            });
        }

        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.PlaceVegetation)), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var code = new List<CodeInstruction>(instructions);

            // in order to declare a local i have to increment all other locals
            // after mine by 1. I also have to make the ilgenerator create a local with the correct index.
            // so, for now the object pool lookup has to be done in GetOrInstantiateObject instead of 
            // at the beginning of the PlaceVegetation method :(
            
            var instantiationIndex = code.FindIndex(c => c.Is(OpCodes.Call, ObjectInstantiateMethod));
            code[instantiationIndex] = new CodeInstruction(OpCodes.Call, GetOrInstantiatePersistentObjectMethod);
            
            return code.AsEnumerable();
        }

        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.SpawnZone)), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> ZoneSystem_SpawnZone_Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var code = new List<CodeInstruction>(instructions);

            var destroyCallIndex = code.FindIndex(instruction => instruction.Is(OpCodes.Call, ObjectDestroyMethod));

            var tempObjectLocal = generator.DeclareLocal(typeof(GameObject));

            code.InsertRange(destroyCallIndex, new[]
            {
                new CodeInstruction(OpCodes.Stloc_S, tempObjectLocal.LocalIndex),
                new CodeInstruction(OpCodes.Ldsfld, VegetationPoolByNameField),
                new CodeInstruction(OpCodes.Ldloc_S, tempObjectLocal.LocalIndex)
            });

            code[destroyCallIndex + 3] = new CodeInstruction(OpCodes.Call, DestroyOrReturnObjectMethod);

            return code.AsEnumerable();
        }

        private static void ReleaseZoneSystemPool()
        {
            VegetationPoolByName.Values.ToList().ForEach(pool => pool.ReleaseAll());
            VegetationPoolByName.Clear();
        }

        private static GameObject GetOrInstantiatePersistentObject(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            return GetOrInstantiateObject(ZoneSystem.SpawnMode.Ghost, PersistentPoolByName, prefab, position, rotation);
        }
    }
}