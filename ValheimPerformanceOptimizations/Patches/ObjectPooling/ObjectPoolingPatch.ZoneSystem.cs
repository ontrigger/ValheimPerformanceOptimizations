using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ValheimPerformanceOptimizations.Patches
{
    public static partial class ObjectPoolingPatch
    {
        public static Dictionary<string, GameObjectPool>
            VegetationPoolByName = new Dictionary<string, GameObjectPool>();

        private static readonly FieldInfo VegetationPoolByNameField =
            AccessTools.DeclaredField(typeof(ObjectPoolingPatch), nameof(VegetationPoolByName));

        private static void CreateZoneSystemPool(Dictionary<ZoneSystem.ZoneVegetation, int> maxObjectsByVegetation)
        {
            VegetationPoolByName = maxObjectsByVegetation.ToDictionary(pair => pair.Key.m_prefab.name, pair =>
            {
                var pool = new GameObjectPool(pair.Key.m_prefab, pair.Value, OnRetrievedFromPool);

                pool.Populate(pair.Value, obj =>
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

                return pool;
            });
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
                new CodeInstruction(OpCodes.Ldsfld, VegetationPoolByNameField)
            });

            // replace the call to instantiate with our method
            code[instantiationIndex + 2 + 4] = new CodeInstruction(OpCodes.Call, GetOrInstantiateObjectMethod);

            return code.AsEnumerable();
        }

        [HarmonyPatch(typeof(ZoneSystem), "SpawnZone"), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> ZoneSystem_SpawnZone_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            var destroyCallIndex = code.FindIndex(instruction => instruction.Is(OpCodes.Call, ObjectDestroyMethod));
            code.Insert(destroyCallIndex, new CodeInstruction(OpCodes.Ldsfld, VegetationPoolByNameField));
            code[destroyCallIndex + 1] = new CodeInstruction(OpCodes.Call, DestroyOrReturnObjectMethod);
            
            return code.AsEnumerable();
        }

        private static void DestroyZoneSystemPool()
        {
            VegetationPoolByName.Values.ToList().ForEach(pool => pool.Destroy());
        }
    }
}