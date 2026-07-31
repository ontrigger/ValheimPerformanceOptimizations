using System.Collections.Generic;
using HarmonyLib;
using Unity.Profiling;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches.ObjectManagement;

/// <summary>
/// Avoids repeatedly resolving an owned ZDOs owner through ZDOExtraData during the
/// servers periodic ownership handoff scan.
/// </summary>
[HarmonyPatch(typeof(ZDOMan), "ReleaseNearbyZDOS")]
internal static class ZDOManReleaseNearbyPatch
{
	private static readonly ProfilerMarker ReleaseNearbyMarker = new("VPO.ZDOMan.ReleaseNearbyZDOS");

	[HarmonyPrefix]
	private static bool Prefix(ZDOMan __instance, Vector3 refPosition, long uid)
	{
		ReleaseNearbyMarker.Begin();
		var zone = ZoneSystem.GetZone(refPosition);
		List<ZDO> nearbyObjects = __instance.m_tempNearObjects;
		nearbyObjects.Clear();
		__instance.FindSectorObjects(zone, ZoneSystem.instance.m_activeArea, 0, nearbyObjects);

		var activatedArea = ZoneSystem.instance.m_activeArea - 1;
		var isServerPass = uid == ZDOMan.GetSessionID();
		for (var i = 0; i < nearbyObjects.Count; i++)
		{
			var zdo = nearbyObjects[i];
			if (!zdo.Persistent) { continue; }

			var sector = zdo.GetSector();
			var hasOwner = zdo.HasOwner();

			long owner;
			bool ownedByPassPeer;
			if (isServerPass)
			{
				ownedByPassPeer = zdo.IsOwner();
				owner = ownedByPassPeer || !hasOwner ? 0L : zdo.GetOwner();
			}
			else
			{
				owner = hasOwner ? zdo.GetOwner() : 0L;
				ownedByPassPeer = owner == uid;
			}

			if (ownedByPassPeer)
			{
				if (!ZNetScene.InActiveArea(sector, zone, activatedArea))
				{
					zdo.SetOwner(0L);
				}

				continue;
			}

			if ((!hasOwner || !__instance.IsInPeerActiveArea(sector, owner))
			    && ZNetScene.InActiveArea(sector, zone, activatedArea))
			{
				zdo.SetOwner(uid);
			}
		}

		ReleaseNearbyMarker.End();
		return false;
	}
}
