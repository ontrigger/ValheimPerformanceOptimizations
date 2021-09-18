using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches
{
    /// <summary>
    ///     ZNetView tries to get the object name by removing the (Clone) part,
    ///     which causes gc allocations along with a ~3ms slowdown in a typical ZoneSystem spawn cycle
    /// </summary>
    [HarmonyPatch]
    public class ZNetViewPrefabNamePatch
    {
        [UsedImplicitly]
        public static string PrefabNameHack;

        private static readonly MethodInfo ObjectInstantiateField =
            AccessTools.GetDeclaredMethods(typeof(Object))
                .Where(m => m.Name == "Instantiate" && m.GetGenericArguments().Length == 1)
                .Select(m => m.MakeGenericMethod(typeof(GameObject)))
                .First(m =>
                           m.GetParameters().Length == 3 &&
                           m.GetParameters()[1].ParameterType == typeof(Vector3));

        private static readonly MethodInfo GameObjectNameGetter = AccessTools.PropertyGetter(
            typeof(GameObject), nameof(GameObject.name));

        private static readonly FieldInfo PrefabNameHackField =
            AccessTools.DeclaredField(typeof(ZNetViewPrefabNamePatch), "PrefabNameHack");

        private static readonly FieldInfo VegetationPrefabField =
            AccessTools.Field(typeof(ZoneSystem.ZoneVegetation), "m_prefab");

        [HarmonyPatch(typeof(ZNetView), nameof(ZNetView.GetPrefabName))]
        private static bool Prefix(ZNetView __instance, ref string __result)
        {
            __result = PrefabNameHack ?? Utils.GetPrefabName(__instance.gameObject);
            return false;
        }

        [HarmonyPatch(typeof(ZNetView), nameof(ZNetView.Awake)), HarmonyPostfix]
        private static void Postfix(ZNetView __instance)
        {
            if (ZNetView.m_ghostInit || __instance == null) return;

            __instance.name = PrefabNameHack ?? Utils.GetPrefabName(__instance.gameObject);
        }

        // GetNrOfInstances expects objects to have (Clone) in the name
        // so we remove that shit
        [HarmonyTranspiler]
        [HarmonyPatch(
            typeof(SpawnSystem), nameof(SpawnSystem.GetNrOfInstances),
            typeof(GameObject), typeof(Vector3),
            typeof(float), typeof(bool), typeof(bool)
        )]
        public static IEnumerable<CodeInstruction> Transpile_SpawnSystem_GetNrOfInstances(
            IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var objGetNameCallIndex = code.FindIndex(c => c.opcode == OpCodes.Callvirt);
            code.RemoveRange(objGetNameCallIndex + 1, 2);

            return code.AsEnumerable();
        }

        // this gets overwritten if object pooling is enabled
        [HarmonyTranspiler, HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.PlaceVegetation))]
        public static IEnumerable<CodeInstruction> Transpile_ZoneSystem_PlaceVegetation(
            IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            var prefixInsertIndex = -1;
            var postfixInsertIndex = -1;
            for (var i = 0; i < code.Count; i++)
            {
                var instruction = code[i];
                if (instruction.Is(OpCodes.Call, ObjectInstantiateField))
                {
                    prefixInsertIndex = i - 4; // quaternion, vector, prefab, vegLocal
                    postfixInsertIndex = i + 2; // after stloc.s

                    break;
                }
            }

            // PrefabNameHack = item.m_prefab.name
            code.InsertRange(prefixInsertIndex, new[]
            {
                new CodeInstruction(OpCodes.Ldloc_S, 5),
                new CodeInstruction(OpCodes.Ldfld, VegetationPrefabField),
                new CodeInstruction(OpCodes.Callvirt, GameObjectNameGetter), //item.m_prefab.name (get_name())
                new CodeInstruction(OpCodes.Stsfld, PrefabNameHackField)
            });

            // PrefabNameHack = null
            // offset to account for newly inserted lines
            code.InsertRange(postfixInsertIndex + 4, new[]
            {
                new CodeInstruction(OpCodes.Ldnull),
                new CodeInstruction(OpCodes.Stsfld, PrefabNameHackField)
            });

            return code.AsEnumerable();
        }
    }
}