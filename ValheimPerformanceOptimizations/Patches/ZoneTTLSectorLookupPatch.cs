using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;

namespace ValheimPerformanceOptimizations.Patches;

/// <summary>
/// vanilla iterates the entire instances dict just to find if an instance is in a sector
/// causes 30ms spikes on bluehills
/// </summary>
[HarmonyPatch]
internal static class ZoneTTLSectorLookupPatch
{
	[HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.UpdateTTL))] [HarmonyPrefix]
	private static bool ZoneSystem_UpdateTTL_Prefix(ZoneSystem __instance, float dt)
	{
		foreach (var zone in __instance.m_zones)
		{
			zone.Value.m_ttl += dt;
		}

		foreach (var zone in __instance.m_zones)
		{
			if (zone.Value.m_ttl > __instance.m_zoneTTL && !HasNearInstanceInSector(ZNetScene.instance, zone.Key))
			{
				Object.Destroy(zone.Value.m_root);
				__instance.m_zones.Remove(zone.Key);
				break;
			}
		}

		return false;
	}

	// very conservative check in neighboring zones to account for interp lag
	private static bool HasNearInstanceInSector(ZNetScene scene, Vector2s sector)
	{
		var objectsBySector = ZDOMan.instance?.m_objectsBySector;
		if (objectsBySector == null) { return scene.HaveInstanceInSector(sector); }

		Profiler.BeginSample("VPO HaveInstanceInSector");
		for (var dy = -1; dy <= 1; dy += 1)
		{
			for (var dx = -1; dx <= 1; dx += 1)
			{
				var index = ZoneSystem.SectorToIndex(sector.x + dx, sector.y + dy).Sector;
				if (index == 0)
				{
					Profiler.EndSample();
					return scene.HaveInstanceInSector(sector);
				}

				var zdos = objectsBySector[index];
				if (zdos == null) { continue; }

				foreach (var zdo in zdos)
				{
					if (!scene.m_instances.TryGetValue(zdo, out var view)) { continue; }

					if (view.m_distant || !view) { continue; }

					if (ZoneSystem.GetZone(view.transform.position) == sector)
					{
						Profiler.EndSample();
						return true;
					}
				}
			}
		}

		Profiler.EndSample();
		return false;
	}
}
