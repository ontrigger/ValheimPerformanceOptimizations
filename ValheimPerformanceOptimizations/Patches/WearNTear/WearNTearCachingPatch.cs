using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;
using ValheimPerformanceOptimizations.Storage;

namespace ValheimPerformanceOptimizations.Patches
{
	public static class WearNTearCachingPatch
	{
		private static int _maskToCheck;

		private static readonly Dictionary<string, Bounds> MaxBoundsForPrefab = new();
		private static readonly Dictionary<Collider, WearNTear> KnownWntByCollider = new();

		private static readonly MethodInfo HaveRoofMethod
			= AccessTools.DeclaredMethod(typeof(WearNTear), nameof(WearNTear.HaveRoof));

		private static readonly MethodInfo GetEnvManInstanceMethod
			= AccessTools.DeclaredMethod(typeof(EnvMan), "get_instance");

		private static readonly MethodInfo IsWetMethod
			= AccessTools.DeclaredMethod(typeof(EnvMan), nameof(EnvMan.IsWet));

		static WearNTearCachingPatch()
		{
			ValheimPerformanceOptimizations.OnInitialized += Initialize;
		}

		private static void Initialize(ConfigFile configFile, Harmony harmony)
		{
			if (ModCompatibility.IsValheimRaftPresent)
			{
				ValheimPerformanceOptimizations.Logger.LogWarning(
					"!!! ValheimRAFT present !!! disabling structural integrity optimizations to maintain compatibility");
			}
			harmony.PatchAll(typeof(WearNTearCachingPatch));
		}

		/// <summary>
		/// adds an IsWet check before trying to call HaveRoof like this
		/// bool flag = true;
		/// if (EnvMan.instance.IsWet())
		/// {
		/// flag = HaveRoof();
		/// }
		/// </summary>
		[HarmonyPatch(typeof(WearNTear), nameof(WearNTear.UpdateWear))] [HarmonyTranspiler]
		private static IEnumerable<CodeInstruction> WearNTear_UpdateWear_Transpiler(
			IEnumerable<CodeInstruction> instructions, ILGenerator generator)
		{
			var code = new List<CodeInstruction>(instructions);

			var getInstanceLabel = code.FindIndex(c => c.Is(OpCodes.Call, GetEnvManInstanceMethod));
			var getInstanceJumpLabel = generator.DefineLabel();
			code[getInstanceLabel].labels.Add(getInstanceJumpLabel);

			var haveRoofCallIndex = code.FindIndex(c => c.Is(OpCodes.Call, HaveRoofMethod));
			code.RemoveRange(haveRoofCallIndex - 1, 2);

			code.Insert(haveRoofCallIndex - 1, new CodeInstruction(OpCodes.Ldc_I4_1));

			code.Insert(haveRoofCallIndex + 1, new CodeInstruction(OpCodes.Call, GetEnvManInstanceMethod));
			code.Insert(haveRoofCallIndex + 2, new CodeInstruction(OpCodes.Callvirt, IsWetMethod));

			var isWetLocal = generator.DeclareLocal(typeof(bool));
			code.InsertRange(haveRoofCallIndex + 3,
				new[]
				{
					new CodeInstruction(OpCodes.Stloc_S, isWetLocal.LocalIndex),
					new CodeInstruction(OpCodes.Ldloc_S, isWetLocal.LocalIndex),
					new CodeInstruction(OpCodes.Brfalse_S, getInstanceJumpLabel),
					new CodeInstruction(OpCodes.Nop),
					new CodeInstruction(OpCodes.Ldarg_0),
					new CodeInstruction(OpCodes.Call, HaveRoofMethod),
					new CodeInstruction(OpCodes.Stloc_1),
					new CodeInstruction(OpCodes.Nop),
				});

			return code.AsEnumerable();
		}

		[HarmonyPatch(typeof(WearNTear), nameof(WearNTear.SetupColliders))] [HarmonyPostfix]
		private static void WearNTear_SetupColliders_Postfix(WearNTear __instance)
		{
			foreach (var collider in __instance.m_colliders)
			{
				KnownWntByCollider[collider] = __instance;
			}
		}

		[HarmonyPatch(typeof(WearNTear), nameof(WearNTear.OnDestroy))] [HarmonyPostfix]
		private static void WearNTear_OnDestroy_Postfix(WearNTear __instance)
		{
			if (__instance.m_colliders == null) { return; }
			
			foreach (var collider in __instance.m_colliders)
			{
				KnownWntByCollider.Remove(collider);
			}
		}
		
		[HarmonyPatch(typeof(WearNTear), nameof(WearNTear.UpdateSupport))] [HarmonyPrefix]
		private static bool UpdateSupport(WearNTear __instance)
		{
			if (__instance.m_colliders == null)
			{
				__instance.SetupColliders();
			}
			__instance.GetMaterialProperties(out var maxSupport, out var _, out var horizontalLoss,
				out var verticalLoss);
			WearNTear.m_tempSupportPoints.Clear();
			WearNTear.m_tempSupportPointValues.Clear();
			var cOM = __instance.GetCOM();
			var a = 0f;
			Profiler.BeginSample("upd bounds");

			HashSet<Collider> encounteredColliders = SetPool<Collider>.Get();
			foreach (var bound in __instance.m_bounds)
			{
				var num = Physics.OverlapBoxNonAlloc(bound.m_pos, bound.m_size, WearNTear.m_tempColliders, bound.m_rot,
					WearNTear.m_rayMask, QueryTriggerInteraction.Ignore);
				for (var i = 0; i < num; i++)
				{
					var collider = WearNTear.m_tempColliders[i];
					if (__instance.m_colliders.Contains(collider) || collider.attachedRigidbody != null
					    || !encounteredColliders.Add(collider))
					{
						continue;
					}

					Profiler.BeginSample("get comp");
					if (!KnownWntByCollider.TryGetValue(collider, out var otherWnt))
					{
						otherWnt = collider.GetComponentInParent<WearNTear>();
						KnownWntByCollider[collider] = otherWnt;
					}
					Profiler.EndSample();

					if (otherWnt == null)
					{
						__instance.m_support = maxSupport;
						__instance.m_nview.GetZDO().Set("support", __instance.m_support);
						Profiler.EndSample();
						return false;
					}
					if (!otherWnt.m_supports)
					{
						continue;
					}

					Profiler.BeginSample("Find support point");
					var num2 = Vector3.Distance(cOM, otherWnt.transform.position) + 0.1f;
					var support = otherWnt.GetSupport();
					a = Mathf.Max(a, support - horizontalLoss * num2 * support);
					Profiler.BeginSample("why");
					var vector = __instance.FindSupportPoint(cOM, otherWnt, collider);
					Profiler.EndSample();
					Profiler.EndSample();

					Profiler.BeginSample("add support points");
					if (vector.y < cOM.y + 0.05f)
					{
						var normalized = (vector - cOM).normalized;
						if (normalized.y < 0f)
						{
							var t = Mathf.Acos(1f - Mathf.Abs(normalized.y)) / ((float)Math.PI / 2f);
							var num3 = Mathf.Lerp(horizontalLoss, verticalLoss, t);
							var b = support - num3 * num2 * support;
							a = Mathf.Max(a, b);
						}
						var item = support - verticalLoss * num2 * support;
						WearNTear.m_tempSupportPoints.Add(vector);
						WearNTear.m_tempSupportPointValues.Add(item);
					}
					Profiler.EndSample();
				}
			}
			SetPool<Collider>.Return(encounteredColliders);
			Profiler.EndSample();

			Profiler.BeginSample("upd support");
			if (WearNTear.m_tempSupportPoints.Count >= 2)
			{
				Profiler.BeginSample("super computin");
				for (var j = 0; j < WearNTear.m_tempSupportPoints.Count; j++)
				{
					var from = WearNTear.m_tempSupportPoints[j] - cOM;
					from.y = 0f;
					for (var k = j + 1; k < WearNTear.m_tempSupportPoints.Count; k++)
					{
						var to = WearNTear.m_tempSupportPoints[k] - cOM;
						to.y = 0f;

						if (Vector3.Angle(from, to) >= 100f)
						{
							var b2 = (WearNTear.m_tempSupportPointValues[j] +
								WearNTear.m_tempSupportPointValues[k]) * 0.5f;
							a = Mathf.Max(a, b2);
						}
					}
				}

				Profiler.EndSample();
			}
			Profiler.EndSample();
			__instance.m_support = Mathf.Min(a, maxSupport);
			__instance.m_nview.GetZDO().Set("support", __instance.m_support);

			return false;
		}
	}
}
