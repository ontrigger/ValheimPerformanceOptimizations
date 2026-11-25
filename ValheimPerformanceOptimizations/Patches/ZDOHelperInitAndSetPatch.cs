using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches
{
	/// <summary>
	/// vanilla InitAndSet binary searches for the key, then on a miss calls Add which searches again.
	/// insert at the index from the first search instead
	/// </summary>
	internal static class ZDOHelperInitAndSetPatch
	{
		private static readonly MethodInfo InitAndSetPrefixMethod = AccessTools.Method(
			typeof(ZDOHelperInitAndSetPatch), nameof(InitAndSetPrefix));

		static ZDOHelperInitAndSetPatch()
		{
			ValheimPerformanceOptimizations.OnInitialized += Initialize;
		}

		private static void Initialize(BepInEx.Configuration.ConfigFile _, Harmony harmony)
		{
			PatchGenericInitAndSet<float>(harmony);
			PatchGenericInitAndSet<Vector3>(harmony);
			PatchGenericInitAndSet<Quaternion>(harmony);
			PatchGenericInitAndSet<int>(harmony);
			PatchGenericInitAndSet<long>(harmony);
			PatchGenericInitAndSet<string>(harmony);

			var byteArrayOriginal = AccessTools.Method(
				typeof(ZDOHelper), nameof(ZDOHelper.InitAndSet),
				new[] { typeof(Dictionary<ZDOID, BinarySearchDictionary<int, byte[]>>), typeof(ZDOID), typeof(int), typeof(byte[]) });
			harmony.Patch(byteArrayOriginal, prefix: new HarmonyMethod(InitAndSetPrefixMethod.MakeGenericMethod(typeof(byte[]))));
		}

		private static void PatchGenericInitAndSet<TValue>(Harmony harmony) where TValue : IEquatable<TValue>
		{
			var original = AccessTools.FirstMethod(typeof(ZDOHelper),
				method => method.Name == nameof(ZDOHelper.InitAndSet) && method.IsGenericMethodDefinition)
				.MakeGenericMethod(typeof(TValue));
			var prefix = InitAndSetPrefixMethod.MakeGenericMethod(typeof(TValue));

			harmony.Patch(original, prefix: new HarmonyMethod(prefix));
		}

		private static bool InitAndSetPrefix<TValue>(
			Dictionary<ZDOID, BinarySearchDictionary<int, TValue>> container, ZDOID zid, int hash, TValue value,
			ref bool __result)
		{
			container.Init(zid);
			var dictionary = container[zid];
			var index = dictionary.BinaryFindKeyIndex(hash, out var exactMatch);
			if (exactMatch)
			{
				if (EqualityComparer<TValue>.Default.Equals(dictionary.m_values[index], value))
				{
					__result = false;
					return false;
				}

				dictionary.m_values[index] = value;
				__result = true;
				return false;
			}

			dictionary.GuaranteeCapacity();
			if (dictionary.m_length - index > 0)
			{
				Array.Copy(dictionary.m_keys, index, dictionary.m_keys, index + 1, dictionary.m_length - index);
				Array.Copy(dictionary.m_values, index, dictionary.m_values, index + 1, dictionary.m_length - index);
			}

			dictionary.m_keys[index] = hash;
			dictionary.m_values[index] = value;
			dictionary.m_length += 1;
			__result = true;
			return false;
		}
	}
}
