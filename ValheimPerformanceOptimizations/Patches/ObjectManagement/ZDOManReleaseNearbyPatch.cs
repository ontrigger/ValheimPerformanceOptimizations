using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;

namespace ValheimPerformanceOptimizations.Patches.ObjectManagement;

/// <summary>
/// reduces chebyshev distance allocs, constant simulation distance rechecks and getowner calls
/// </summary>
[HarmonyPatch(typeof(ZDOMan), "ReleaseNearbyZDOS")]
internal static class ZDOManReleaseNearbyPatch
{
	[HarmonyPrefix]
	private static bool Prefix(ZDOMan __instance, Vector3 refPosition, long uid)
	{
		Profiler.BeginSample("ReleaseNearbyZDOS");
		var zone = ZoneSystem.GetZone(refPosition);
		var syncedSimulationDistance = ZNet.instance.GetSyncedSimulationDistance();
		var zoneSize = ZoneSystem.instance.m_zoneSize;
		var zonePosition = ZoneSystem.GetZonePos(zone);
		List<ZDO> nearbyObjects = __instance.m_tempNearObjects;
		nearbyObjects.Clear();
		var nearSimulationDistance = new SimulationDistance(
			syncedSimulationDistance.NearSimulationDistance,
			0,
			syncedSimulationDistance.IsClassic);
		__instance.FindSectorObjects(zone, nearSimulationDistance, nearbyObjects);

		var sessionId = ZDOMan.GetSessionID();
		var isServerPass = uid == sessionId;
		for (var i = 0; i < nearbyObjects.Count; i++)
		{
			var zdo = nearbyObjects[i];
			if (!zdo.Persistent) { continue; }

			var position = zdo.GetPosition();
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
				if (!IsInActiveArea(position, zonePosition, syncedSimulationDistance, zoneSize))
				{
					zdo.SetOwner(0L);
				}

				continue;
			}

			if ((!hasOwner || !IsInPeerActiveArea(
				    position, owner, sessionId, syncedSimulationDistance, zoneSize))
			    && IsInActiveArea(position, zonePosition, syncedSimulationDistance, zoneSize))
			{
				zdo.SetOwner(uid);
			}
		}

		Profiler.EndSample();

		return false;
	}

	private static bool IsInPeerActiveArea(
		Vector3 point,
		long uid,
		long sessionId,
		SimulationDistance simulationDistance,
		float zoneSize)
	{
		Vector3 referencePosition;
		if (uid == sessionId)
		{
			referencePosition = ZNet.instance.GetReferencePosition();
		}
		else
		{
			var peer = ZNet.instance.GetPeer(uid);
			if (peer == null) { return false; }

			referencePosition = peer.GetRefPos();
		}

		var zone = ZoneSystem.GetZone(referencePosition);
		return IsInActiveArea(point, ZoneSystem.GetZonePos(zone), simulationDistance, zoneSize);
	}

	private static bool IsInActiveArea(
		Vector3 point, Vector3 zonePosition, SimulationDistance simulationDistance, float zoneSize)
	{
		point.y = 0f;
		var maxChebyshevDistance =
			(simulationDistance.NearSimulationDistance == 1 ? 1f : 1.5f) * zoneSize;
		if (Utils.ChebyshevDistance(zonePosition, point) > maxChebyshevDistance)
		{
			return false;
		}

		if (simulationDistance.NearSimulationDistance == 2 && !simulationDistance.IsClassic)
		{
			var maxDistance = zoneSize * 1.75f;
			return (zonePosition - point).sqrMagnitude < maxDistance * maxDistance;
		}

		return true;
	}

	// this allocd 1.5mb lmfao
	[HarmonyPatch(typeof(Utils), nameof(Utils.ChebyshevDistance), typeof(Vector3), typeof(Vector3))]
	private static class ChebyshevDistanceAllocPatch
	{
		[HarmonyPrefix]
		private static bool Prefix(Vector3 a, Vector3 b, ref float __result)
		{
			var x = Mathf.Abs(a.x - b.x);
			var y = Mathf.Abs(a.y - b.y);
			var z = Mathf.Abs(a.z - b.z);

			// Use the two-argument overload so the params-array overload does not allocate.
			__result = Mathf.Max(Mathf.Max(x, y), z);
			return false;
		}
	}
}
