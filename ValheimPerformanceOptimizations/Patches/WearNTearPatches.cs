using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;
using Unity.Profiling;

namespace ValheimPerformanceOptimizations.Patches
{
	[HarmonyPatch]
	internal static class WearNTearPatches
	{
		private class CachedOwner
		{
			[CanBeNull] public WearNTear Owner;
		}

		private class CachedWearNTear
		{
			public readonly HashSet<Collider> Values = new();
			public bool HasCenterOfMass;
			public Vector3 CenterOfMass;
		}

		private static readonly ConditionalWeakTable<Collider, CachedOwner> Owners = new();
		private static readonly ConditionalWeakTable<WearNTear, CachedWearNTear> WearNTearCaches = new();
		private static readonly Stack<CachedWearNTear> WearNTearCachePool = new();
		private static readonly HashSet<Collider> ProcessedSupportColliders = new();
		private static readonly List<CachedWearNTear> CachedCentersOfMass = new();

		private static WearNTear GetOrCacheOwner(Collider collider)
		{
			if (collider == null)
			{
				return null;
			}

			if (Owners.TryGetValue(collider, out var cached))
			{
				return cached.Owner;
			}

			var owner = collider.GetComponentInParent<WearNTear>();
			Owners.Add(collider, new CachedOwner
			{
				Owner = owner,
			});
			return owner;
		}

		private static CachedWearNTear GetOrCreateWearNTearCache(WearNTear instance)
		{
			if (WearNTearCaches.TryGetValue(instance, out var cached))
			{
				return cached;
			}

			cached = WearNTearCachePool.Count > 0
				? WearNTearCachePool.Pop()
				: new CachedWearNTear();
			WearNTearCaches.Add(instance, cached);
			return cached;
		}

		private static void ReturnWearNTearCache(CachedWearNTear cached)
		{
			cached.Values.Clear();
			cached.HasCenterOfMass = false;
			cached.CenterOfMass = default;
			WearNTearCachePool.Push(cached);
		}

		private static HashSet<Collider> GetOwnColliders(WearNTear owner)
		{
			var cached = GetOrCreateWearNTearCache(owner);

			if (cached.Values.Count == 0)
			{
				foreach (var ownCollider in owner.m_colliders)
				{
					cached.Values.Add(ownCollider);
				}
			}

			return cached.Values;
		}

		private static readonly ProfilerMarker UpdateSupportMarker = new("VPO.WearNTear.UpdateSupport");

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static float GetOptimizedSupport(WearNTear instance)
		{
			var zdo = instance.m_nview.GetZDO();
			if (zdo == null || !zdo.IsValid() || !zdo.HasOwner())
			{
				return instance.GetMaxSupport();
			}

			if (zdo.IsOwner())
			{
				return instance.m_support;
			}

			return zdo.GetFloat(ZDOVars.s_support, out var support)
				? support
				: instance.GetMaxSupport();
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static Vector3 GetOptimizedCOM(WearNTear instance)
		{
			var transform = instance.transform;
			return transform.position + transform.rotation * instance.m_comOffset;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static Vector3 GetCachedCOM(WearNTear instance)
		{
			var cached = GetOrCreateWearNTearCache(instance);

			if (!cached.HasCenterOfMass)
			{
				cached.HasCenterOfMass = true;
				cached.CenterOfMass = GetOptimizedCOM(instance);
				CachedCentersOfMass.Add(cached);
			}

			return cached.CenterOfMass;
		}

		[HarmonyPrefix]
		[HarmonyPatch(typeof(WearNTear), "UpdateSupport")]
		private static bool UpdateSupportPrefix(WearNTear __instance)
		{
			UpdateSupportMarker.Begin();

			var count = __instance.m_supportColliders.Count;
			if (count > 0)
			{
				var num = 0;
				var num2 = 0f;
				for (var i = 0; i < count; i++)
				{
					var collider = __instance.m_supportColliders[i];
					if (collider == null)
					{
						break;
					}

					var componentInParent = GetOrCacheOwner(collider);
					if (componentInParent == null || !componentInParent.m_supports)
					{
						break;
					}

					if (collider.transform.position == __instance.m_supportPositions[i])
					{
						var support = GetOptimizedSupport(componentInParent);
						if (support > num2)
						{
							num2 = support;
						}

						if (support.Equals(__instance.m_supportValue[i]))
						{
							num++;
						}
					}
				}

				if (num == __instance.m_supportPositions.Count && num2 > __instance.m_support)
				{
					return false;
				}

				__instance.ClearCachedSupport();
			}

			if (__instance.m_colliders == null)
			{
				__instance.SetupColliders();
			}

			var ownColliders = GetOwnColliders(__instance);
			__instance.GetMaterialProperties(out var maxSupport, out _, out var horizontalLoss, out var verticalLoss);
			WearNTear.s_tempSupportPoints.Clear();
			WearNTear.s_tempSupportPointValues.Clear();
			ProcessedSupportColliders.Clear();
			var cOM = GetOptimizedCOM(__instance);
			var flag = false;
			var num3 = 0f;

			foreach (var bound in __instance.m_bounds)
			{
				var num4 = Physics.OverlapBoxNonAlloc(
					bound.m_pos,
					bound.m_size,
					WearNTear.s_tempColliders,
					bound.m_rot,
					WearNTear.s_rayMask);

				if (__instance.m_clearCachedSupport)
				{
					for (var i = 0; i < num4; i++)
					{
						var collider = WearNTear.s_tempColliders[i];
						if (collider.attachedRigidbody != null || collider.isTrigger ||
							ownColliders.Contains(collider))
						{
							continue;
						}

						var componentInParent = GetOrCacheOwner(collider);
						if (componentInParent == null)
						{
							continue;
						}

						if (componentInParent.m_nview.IsOwner())
						{
							componentInParent.ClearCachedSupport();
						}
						else if (componentInParent.m_nview.IsValid())
						{
							componentInParent.m_nview.InvokeRPC(
								componentInParent.m_nview.GetZDO().GetOwner(),
								"RPC_ClearCachedSupport");
						}
					}

					__instance.m_clearCachedSupport = false;
				}

				for (var i = 0; i < num4; i++)
				{
					var collider = WearNTear.s_tempColliders[i];
					if (collider.attachedRigidbody != null || collider.isTrigger ||
						ownColliders.Contains(collider))
					{
						continue;
					}

					if (!ProcessedSupportColliders.Add(collider))
					{
						continue;
					}

					if (collider.gameObject.layer == WearNTear.s_terrainLayer)
					{
						flag = true;
						continue;
					}

					var componentInParent = GetOrCacheOwner(collider);
					if (componentInParent == null)
					{
						__instance.m_support = maxSupport;
						__instance.ClearCachedSupport();
						__instance.m_nview.GetZDO().Set(ZDOVars.s_support, __instance.m_support);
						return false;
					}

					if (!componentInParent.m_supports)
					{
						continue;
					}

					var num5 = Vector3.Distance(
						cOM,
						GetCachedCOM(componentInParent)) + 0.1f;
					var num6 = Vector3.Distance(cOM, componentInParent.transform.position) + 0.1f;
					if (num6 < num5 && !__instance.m_forceCorrectCOMCalculation)
					{
						num5 = num6;
					}

					var support = GetOptimizedSupport(componentInParent);
					num3 = Mathf.Max(num3, support - horizontalLoss * num5 * support);

					var vector = WearNTear.FindSupportPoint(cOM, componentInParent, collider);
					if (vector.y < cOM.y + 0.05f)
					{
						var normalized = (vector - cOM).normalized;
						if (normalized.y < 0f)
						{
							var t = Mathf.Acos(1f - Mathf.Abs(normalized.y)) / (MathF.PI / 2f);
							var num7 = Mathf.Lerp(horizontalLoss, verticalLoss, t);
							var b = support - num7 * num5 * support;
							num3 = Mathf.Max(num3, b);
						}

						var item = support - verticalLoss * num5 * support;
						WearNTear.s_tempSupportPoints.Add(vector);
						WearNTear.s_tempSupportPointValues.Add(item);
						__instance.m_supportColliders.Add(collider);
						__instance.m_supportPositions.Add(collider.transform.position);
						__instance.m_supportValue.Add(support);
					}
				}
			}

			if (flag)
			{
				__instance.m_support = maxSupport;
				__instance.m_nview.GetZDO().Set(ZDOVars.s_support, __instance.m_support);
				return false;
			}

			if (WearNTear.s_tempSupportPoints.Count > 0)
			{
				var count2 = WearNTear.s_tempSupportPoints.Count;
				for (var i = 0; i < count2 - 1; i++)
				{
					var vector2 = WearNTear.s_tempSupportPoints[i] - cOM;
					vector2.y = 0f;
					for (var j = i + 1; j < count2; j++)
					{
						var num8 = (WearNTear.s_tempSupportPointValues[i] +
							WearNTear.s_tempSupportPointValues[j]) * 0.5f;
						if (num8 <= num3)
						{
							continue;
						}

						var to = WearNTear.s_tempSupportPoints[j] - cOM;
						to.y = 0f;
						if (Vector3.Angle(vector2, to) >= 100f)
						{
							num3 = num8;
						}
					}
				}
			}

			__instance.m_support = Mathf.Min(num3, maxSupport);
			__instance.m_nview.GetZDO().Set(ZDOVars.s_support, __instance.m_support);
			if (!__instance.HaveSupport())
			{
				__instance.ClearCachedSupport();
			}

			return false;
		}

		[HarmonyPostfix]
		[HarmonyPatch(typeof(WearNTear), "OnDestroy")]
		private static void WearNTearOnDestroyPostfix(WearNTear __instance)
		{
			if (!WearNTearCaches.TryGetValue(__instance, out var cached))
			{
				return;
			}

			WearNTearCaches.Remove(__instance);
			CachedCentersOfMass.Remove(cached);
			ReturnWearNTearCache(cached);
		}

		[HarmonyFinalizer]
		[HarmonyPatch(typeof(WearNTear), "UpdateSupport")]
		private static Exception UpdateSupportProfilerFinalizer(Exception __exception)
		{
			ProcessedSupportColliders.Clear();
			foreach (var cached in CachedCentersOfMass)
			{
				cached.HasCenterOfMass = false;
			}

			CachedCentersOfMass.Clear();
			UpdateSupportMarker.End();
			return __exception;
		}
	}
}
