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
            PersistentPoolByName = new Dictionary<string, GameObjectPool>();
        
        public static int FreePoolObjects => PersistentPoolByName.Sum(pair => pair.Value.PooledObjects);
        
        public static int UsedPoolObjects => PersistentPoolByName.Sum(pair => pair.Value.MaxObjects - pair.Value.PooledObjects);
        public static int MaxPooledObjects => PersistentPoolByName.Sum(pair => pair.Value.MaxObjects);

        private static readonly FieldInfo PersistentPoolByNameField =
            AccessTools.DeclaredField(typeof(ObjectPoolingPatch), nameof(PersistentPoolByName));

        private static void CreateZNetScenePool(Dictionary<ZoneSystem.ZoneVegetation, int> maxObjectsByVegetation)
        {
            var buildPiecePools = ZNetScene.instance.m_prefabs.Where(prefab =>
            {
                return (prefab.name.StartsWith("wood") || prefab.name.StartsWith("stone")) 
                       && prefab.GetComponent<Piece>();
            }).Select(prefab =>
            {
                ExtractPrefabProcessors(prefab);
                
                var maxObjects = prefab.name.StartsWith("raise") ? Math.Max(_pooledObjectCount.Value, 2000) : _pooledObjectCount.Value;
                var pool = new GameObjectPool(prefab, maxObjects, OnRetrievedFromPool, OnReturnedToPool);

                return new KeyValuePair<string, GameObjectPool>(prefab.name, pool);
            }).ToList();
            
            var vegetationPools = maxObjectsByVegetation.Select(pair => 
            {
                var pool = new GameObjectPool(pair.Key.m_prefab, pair.Value * 3, OnRetrievedFromPool, OnReturnedToPool);

                return new KeyValuePair<string, GameObjectPool>(pair.Key.m_prefab.name, pool);
            }).ToList();

            buildPiecePools.ForEach(pair => PersistentPoolByName.Add(pair.Key, pair.Value));
            vegetationPools.ForEach(pair => PersistentPoolByName.Add(pair.Key, pair.Value));
        }

        [HarmonyPatch(typeof(ZNetScene), "RemoveObjects"), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> ZNetScene_RemoveObjects_Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var code = new List<CodeInstruction>(instructions);

            var destroyCallIndex = code.FindIndex(instruction => instruction.Is(OpCodes.Call, ObjectDestroyMethod));
            
            // due to assembly optimization, the znetview object is taken from the top of the stack
            // instead of being used as a local. I have to create a local for it because the Ldsfld code above 
            // takes up the top value on the stack
            var zNetViewLocal = generator.DeclareLocal(typeof(GameObject));
            
            // replace dup with local store
            code[destroyCallIndex - 6] = new CodeInstruction(OpCodes.Stloc_S, zNetViewLocal.LocalIndex);
            
            // add local load before GetZDO
            code.Insert(destroyCallIndex - 5, new CodeInstruction(OpCodes.Ldloc_S, zNetViewLocal.LocalIndex));
            
            code[destroyCallIndex - 2] = new CodeInstruction(OpCodes.Ldloc_S, zNetViewLocal.LocalIndex);

            code.InsertRange(destroyCallIndex, new []
            {
                new CodeInstruction(OpCodes.Ldsfld, PersistentPoolByNameField),
                
                // add local load before get_GameObject
                new CodeInstruction(OpCodes.Ldloc_S, zNetViewLocal.LocalIndex) // zNetView local 
            });

            code[destroyCallIndex + 3] = new CodeInstruction(OpCodes.Call, DestroyOrReturnObjectMethod);

            return code.AsEnumerable();
        }

        [HarmonyPatch(typeof(ZNetScene), "CreateObject"), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> ZNetScene_CreateObject_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            var instantiateCallIndex =
                code.FindIndex(instruction => instruction.Is(OpCodes.Call, ObjectInstantiateMethod));
            // prepend the spawnmode and poolDict to args
            code.InsertRange(instantiateCallIndex - 3, new[]
            {
                new CodeInstruction(OpCodes.Ldc_I4_2),
                new CodeInstruction(OpCodes.Ldsfld, PersistentPoolByNameField)
            });

            code[instantiateCallIndex + 2] = new CodeInstruction(OpCodes.Call, GetOrInstantiateObjectMethod);
            
            return code.AsEnumerable();
        }

        private static void ReleaseZNetScenePool()
        {
            PersistentPoolByName.Values.ToList().ForEach(pool => pool.ReleaseAll());
            PersistentPoolByName.Clear();
        }
    }
}