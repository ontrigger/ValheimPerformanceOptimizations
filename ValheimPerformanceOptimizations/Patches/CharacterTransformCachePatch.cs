using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches
{
	/// <summary>
	/// caches get_transform calls avoiding unmarshalling from native
	/// </summary>
	//[HarmonyPatch]
	internal static class CharacterTransformCachePatch
	{
		private static Character _cachedCharacter;

		private static Transform _cachedTransform;

		private static bool _cacheActive;

		private static IEnumerable<MethodBase> TargetMethods()
		{
			foreach (var type in GetCharacterTypes())
			{
				if (type.ContainsGenericParameters)
				{
					continue;
				}

				var method = type.GetMethod(
					nameof(Character.CustomFixedUpdate),
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
					null,
					[typeof(float)],
					null);

				if (method != null && !method.ContainsGenericParameters)
				{
					yield return method;
				}
			}
		}

		[HarmonyPrefix]
		private static void Prefix(Character __instance, out bool __state)
		{
			// Humanoid.CustomFixedUpdate calls Character.CustomFixedUpdate. Only the
			// outermost method owns the cache so the nested base call cannot clear it.
			__state = !_cacheActive;
			if (!__state) { return; }

			_cachedCharacter = __instance;
			_cachedTransform = __instance.transform;
			_cacheActive = true;
		}

		[HarmonyFinalizer]
		private static Exception Finalizer(Exception __exception, bool __state)
		{
			if (__state)
			{
				_cachedCharacter = null;
				_cachedTransform = null;
				_cacheActive = false;
			}

			return __exception;
		}

		internal static Transform GetCachedTransform(Character character)
		{
			return _cacheActive && ReferenceEquals(_cachedCharacter, character)
				? _cachedTransform
				: character.transform;
		}

		private static IEnumerable<Type> GetCharacterTypes()
		{
			var characterAssembly = typeof(Character).Assembly;
			foreach (var type in AccessTools.GetTypesFromAssembly(characterAssembly))
			{
				if (type == typeof(Character) || type.IsSubclassOf(typeof(Character)))
				{
					yield return type;
				}
			}
		}
	}

	//[HarmonyPatch]
	internal static class CharacterTransformCacheTranspiler
	{
		private static readonly MethodInfo TransformGetter = AccessTools.PropertyGetter(
			typeof(Component), nameof(Component.transform));

		private static readonly MethodInfo GetCachedTransformMethod = AccessTools.Method(
			typeof(CharacterTransformCachePatch),
			nameof(CharacterTransformCachePatch.GetCachedTransform),
			new[] { typeof(Character) });

		private static IEnumerable<MethodBase> TargetMethods()
		{
			var characterAssembly = typeof(Character).Assembly;
			foreach (var type in AccessTools.GetTypesFromAssembly(characterAssembly))
			{
				if ((type != typeof(Character) && !type.IsSubclassOf(typeof(Character))) || type.ContainsGenericParameters)
				{
					continue;
				}

				foreach (var method in AccessTools.GetDeclaredMethods(type))
				{
					if (IsSupportedMethod(method))
					{
						yield return method;
					}
				}
			}
		}

		private static bool IsSupportedMethod(MethodBase method)
		{
			return !method.IsStatic
				&& !method.IsAbstract
				&& !method.ContainsGenericParameters
				&& method.GetMethodBody() != null;
		}

		[HarmonyTranspiler]
		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			var code = new List<CodeInstruction>(instructions);
			for (var i = 1; i < code.Count; i++)
			{
				if (!code[i].Calls(TransformGetter) || code[i - 1].opcode != OpCodes.Ldarg_0)
				{
					continue;
				}

				// Keep ldarg.0 on the stack and replace Component.get_transform with
				// GetCachedTransform(Character), preserving the original stack shape.
				code[i].opcode = OpCodes.Call;
				code[i].operand = GetCachedTransformMethod;
			}

			return code;
		}
	}
}
