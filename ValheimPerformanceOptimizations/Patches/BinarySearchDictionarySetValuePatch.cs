using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches
{
	/// <summary>
	/// fixes boxing in BinarySearchDictionary.SetValue. this saves 6-7kb of alloc in Update
	/// </summary>
	internal static class BinarySearchDictionarySetValuePatch
	{
		private static readonly MethodInfo SetValuePrefixMethod = AccessTools.Method(
			typeof(BinarySearchDictionarySetValuePatch), nameof(SetValuePrefix));

		static BinarySearchDictionarySetValuePatch()
		{
			ValheimPerformanceOptimizations.OnInitialized += Initialize;
		}

		private static void Initialize(BepInEx.Configuration.ConfigFile _, Harmony harmony)
		{
			PatchSetValue<float>(harmony);
			PatchSetValue<Vector3>(harmony);
			PatchSetValue<Quaternion>(harmony);
			PatchSetValue<int>(harmony);
			PatchSetValue<long>(harmony);
			PatchSetValue<string>(harmony);
			PatchSetValue<byte[]>(harmony);
		}

		private static void PatchSetValue<TValue>(Harmony harmony)
		{
			var dictionaryType = typeof(BinarySearchDictionary<int, TValue>);
			var original = AccessTools.Method(
				dictionaryType, nameof(BinarySearchDictionary<int, TValue>.SetValue),
				new[] { typeof(int), typeof(TValue) });
			var prefix = SetValuePrefixMethod.MakeGenericMethod(typeof(TValue));

			harmony.Patch(original, prefix: new HarmonyMethod(prefix));
		}

		private static bool SetValuePrefix<TValue>(
			BinarySearchDictionary<int, TValue> __instance, int key, TValue value, ref bool __result)
		{
			var index = __instance.BinaryFindKeyIndex(key, out var exactMatch);
			if (exactMatch)
			{
				if (EqualityComparer<TValue>.Default.Equals(__instance.m_values[index], value))
				{
					__result = false;
					return false;
				}

				__instance.m_values[index] = value;
				__result = true;
				return false;
			}

			__instance.GuaranteeCapacity();
			if (__instance.m_length - index > 0)
			{
				Array.Copy(__instance.m_keys, index, __instance.m_keys, index + 1,
					__instance.m_length - index);
				Array.Copy(__instance.m_values, index, __instance.m_values, index + 1,
					__instance.m_length - index);
			}

			__instance.m_keys[index] = key;
			__instance.m_values[index] = value;
			__instance.m_length++;
			__result = true;
			return false;
		}
	}
}
