using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches;

/// <summary>
/// just grab the zone by pos instead of linear iterating all zones. -15ms in bluehills
/// </summary>
[HarmonyPatch]
internal static class LocationZoneCachePatch
{
	private static readonly Dictionary<Vector2s, Location> LocationByZone = new();

	[HarmonyPatch(typeof(Location), nameof(Location.Awake))] [HarmonyPostfix]
	private static void Location_Awake_Postfix(Location __instance)
	{
		LocationByZone[ZoneSystem.GetZone(__instance.transform.position)] = __instance;
	}

	[HarmonyPatch(typeof(Location), nameof(Location.OnDestroy))] [HarmonyPrefix]
	private static void Location_OnDestroy_Prefix(Location __instance)
	{
		var zone = ZoneSystem.GetZone(__instance.transform.position);
		LocationByZone.Remove(zone);
	}

	[HarmonyPatch(typeof(Location), nameof(Location.GetZoneLocation), typeof(Vector2s))] [HarmonyPrefix]
	private static bool Location_GetZoneLocation_Prefix(Vector2s zone, ref Location __result)
	{
		LocationByZone.TryGetValue(zone, out __result);
		return false;
	}

	[HarmonyPatch(typeof(Location), nameof(Location.GetZoneLocation), typeof(Vector3))] [HarmonyPrefix]
	private static bool Location_GetZoneLocationByPosition_Prefix(
		Vector3 point, ref Location __result)
	{
		var zone = ZoneSystem.GetZone(point);
		LocationByZone.TryGetValue(zone, out __result);
		return false;
	}
}
