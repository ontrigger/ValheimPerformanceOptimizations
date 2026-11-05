using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;

namespace ValheimPerformanceOptimizations.Patches;

// vanilla staticphysics is useless, it checks for solids from super high above, 
// tracks every single hit, sorts upon insertion and then doesn't even use the right result lmfao
// so not only do branches float if something is above them but they can also just randomly float if something is below them as well

// so we just do a short raycast slightly above the prop and if it hits anything then that is considered supported by vanilla
// 11ms to 3ms. ideally we would raycastcommand this but it requires more structural changes
[HarmonyPatch]
internal static class StaticPhysicsSupportCheckPatch
{
	private const float FallTolerance = 0.05f;
	private const float StartAbove = 0.1f;
	private const float ExtraDistance = 0.05f;

	private static readonly RaycastHit[] Hits = new RaycastHit[32];

	[HarmonyPrefix]
	[HarmonyPatch(typeof(StaticPhysics), nameof(StaticPhysics.CheckFall))] 
	private static bool StaticPhysics_CheckFall_Prefix(StaticPhysics __instance)
	{
		if (!__instance.m_checkSolids)
		{
			return true;
		}

		Profiler.BeginSample("StaticPhysics short support cast");
		var supported = IsSupported(__instance.transform, __instance.m_fallCheckRadius);
		Profiler.EndSample();

		return !supported;
	}

	private static bool IsSupported(Transform transform, float radius)
	{
		var position = transform.position;
		var minHeight = position.y - FallTolerance;
		var mask = ZoneSystem.instance.m_solidRayMask;

		int count;
		if (radius <= 0f)
		{
			var origin = position + Vector3.up * StartAbove;
			var distance = StartAbove + FallTolerance + ExtraDistance;
			count = Physics.RaycastNonAlloc(origin, Vector3.down, Hits, distance, mask);
		}
		else
		{
			var origin = position + Vector3.up * (radius + StartAbove);
			var distance = StartAbove + FallTolerance + ExtraDistance;
			count = Physics.SphereCastNonAlloc(origin, radius, Vector3.down, Hits, distance, mask);
		}

		for (var i = 0; i < count; i += 1)
		{
			var hit = Hits[i];
			var collider = hit.collider;
			if (collider.attachedRigidbody != null || Utils.IsParent(collider.transform, transform))
			{
				continue;
			}

			if (hit.distance <= 0f || hit.point.y >= minHeight)
			{
				return true;
			}
		}

		return false;
	}
}
