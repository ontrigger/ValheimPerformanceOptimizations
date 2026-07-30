using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace ValheimPerformanceOptimizations.Patches
{
	/// <summary>
	/// orig calls GetInt like a billion times which looks up ints for this zdo then gets the int
	/// so get the zdo ints once and grab the ints from it instead
	/// </summary>
	[HarmonyPatch(typeof(VisEquipment), nameof(VisEquipment.UpdateEquipmentVisuals))]
	internal static class VisEquipmentDataPatch
	{
		private static readonly MethodInfo ZDOGetInt = AccessTools.Method(
			typeof(ZDO), nameof(ZDO.GetInt), new[] { typeof(int), typeof(int) });

		private static readonly MethodInfo GetCachedIntMethod = AccessTools.Method(
			typeof(VisEquipmentDataPatch), nameof(GetCachedInt));

		private static BinarySearchDictionary<int, int> _currentIntData;

		[HarmonyPrefix]
		private static void Prefix(VisEquipment __instance, out BinarySearchDictionary<int, int> __state)
		{
			__state = _currentIntData;

			var zdo = __instance.m_nview.GetZDO();
			_currentIntData = null;
			if (zdo != null)
			{
				ZDOExtraData.s_ints.TryGetValue(zdo.m_uid, out _currentIntData);
			}
		}

		[HarmonyTranspiler]
		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			foreach (var instruction in instructions)
			{
				if (instruction.Calls(ZDOGetInt))
				{
					instruction.opcode = OpCodes.Call;
					instruction.operand = GetCachedIntMethod;
				}

				yield return instruction;
			}
		}

		[HarmonyFinalizer]
		private static Exception Finalizer(
			Exception __exception, BinarySearchDictionary<int, int> __state)
		{
			_currentIntData = __state;
			return __exception;
		}

		private static int GetCachedInt(ZDO _, int hash, int defaultValue)
		{
			return _currentIntData?.GetValueOrDefault(hash, defaultValue) ?? defaultValue;
		}
	}
}
