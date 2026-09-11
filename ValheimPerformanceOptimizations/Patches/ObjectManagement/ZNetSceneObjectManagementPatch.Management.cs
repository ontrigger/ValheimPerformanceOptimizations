using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using ValheimPerformanceOptimizations.Extensions;
using Object = UnityEngine.Object;

namespace ValheimPerformanceOptimizations.Patches.ObjectManagement;

public static partial class ZNetSceneObjectManagementPatch
{
	private static Vector2s _currentZone;
	private static SimulationDistance _currentSimulationDistance;
	private static bool _cacheInitialized;
	private static bool _nearObjectsNeedSort;
	private static readonly ProfilerMarker RebuildObjectCacheMarker =
		new("VPO.ZNetSceneObjectManagement.RebuildObjectCache");
	private static readonly ProfilerMarker ApplyPendingMembershipUpdatesMarker =
		new("VPO.ZNetSceneObjectManagement.ApplyPendingMembershipUpdates");
	private static readonly ProfilerMarker SortNearObjectsMarker =
		new("VPO.ZNetSceneObjectManagement.SortNearObjects");
	private static readonly ProfilerMarker DestroyQueuedObjectsMarker =
		new("VPO.ZNetSceneObjectManagement.DestroyQueuedObjects");

	private static readonly List<QueuedZDO> QueuedNearObjects = new();
	private static readonly List<QueuedZDO> QueuedDistantObjects = new();
	private static readonly Dictionary<ZDOID, QueuedZDO> PendingRemovalObjects = new();
	private static readonly HashSet<ZDOID> PendingMembershipIds = new();
	private static readonly HashSet<ZDOID> InvalidatedZdoIds = new();
	private static readonly Dictionary<ZDOID, int> QueuedNearObjectIndices = new();
	private static readonly Dictionary<ZDOID, int> QueuedDistantObjectIndices = new();
	private static readonly Func<QueuedZDO, ZDOID> QueuedZdoIdSelector = static entry => entry.Id;

	private readonly struct QueuedZDO
	{
		public readonly ZDOID Id;
		public readonly ZDO ZDO;

		public QueuedZDO(ZDO zdo)
		{
			Id = zdo.m_uid;
			ZDO = zdo;
		}
	}

	private enum AreaMembership : byte
	{
		None,
		Near,
		Distant,
	}

	private static bool CreateDestroyObjects(ZNetScene scene)
	{
		var referencePosition = ZNet.instance.GetReferencePosition();
		var zone = ZoneSystem.GetZone(referencePosition);
		var simulationDistance = ZNet.instance.GetSyncedSimulationDistance();
		ApplyPendingZDOAreaUpdates();

		var shouldRebuild = !_cacheInitialized
			|| zone.x != _currentZone.x || zone.y != _currentZone.y
			|| !simulationDistance.Equals(_currentSimulationDistance);

		if (shouldRebuild)
		{
			RebuildObjectCache(zone, simulationDistance);
			_currentZone = zone;
			_currentSimulationDistance = simulationDistance;
			_cacheInitialized = true;
			_nearObjectsNeedSort = true;
		}

		var maxCreatedPerFrame = scene.InLoadingScreen() ? 100 : 10;
		var created = 0;
		
		CreateNearObjects(scene, referencePosition, maxCreatedPerFrame, ref created);
		CreateDistantObjects(scene, maxCreatedPerFrame, ref created);
		DestroyQueuedObjects(scene);

		return false;
	}

	private static void RebuildObjectCache(Vector2s zone, SimulationDistance simulationDistance)
	{
		RebuildObjectCacheMarker.Begin();
		Profiler.BeginSample("BuildActiveSectorSets");
		BuildActiveZoneSets(zone, simulationDistance);
		Profiler.EndSample();

		Profiler.BeginSample("CollectChangedZones");
		ChangedZones.Clear();
		CollectChangedZones(ActiveNearZones, NextNearZones);
		CollectChangedZones(ActiveDistantZones, NextDistantZones);
		Profiler.EndSample();

		Profiler.BeginSample("UpdateZoneObjectMemberships");
		foreach (var changedZone in ChangedZones)
		{
			var previousMembership = GetZoneMembership(
				changedZone, ActiveNearZones, ActiveDistantZones);
			var nextMembership = GetZoneMembership(
				changedZone, NextNearZones, NextDistantZones);
			UpdateZoneObjectMemberships(changedZone, previousMembership, nextMembership);
		}
		Profiler.EndSample();

		Profiler.BeginSample("PublishActiveZoneSets");
		ActiveNearZones.Clear();
		ActiveNearZones.UnionWith(NextNearZones);
		ActiveDistantZones.Clear();
		ActiveDistantZones.UnionWith(NextDistantZones);
		Profiler.EndSample();
		RebuildObjectCacheMarker.End();
	}

	private static void CreateNearObjects(
		ZNetScene scene, Vector3 referencePosition, int maxCreatedPerFrame, ref int created)
	{
		if (!ZoneSystem.instance.IsActiveAreaLoaded()) { return; }

		if (QueuedNearObjects.Count == 0) { return; }

		var targetCount = Mathf.Max(QueuedNearObjects.Count / 100, maxCreatedPerFrame);
		if (_nearObjectsNeedSort)
		{
			SortNearObjects(referencePosition);
		}
		for (var i = QueuedNearObjects.Count - 1; i >= 0; i--)
		{
			var entry = QueuedNearObjects[i];
			var zdo = entry.ZDO;
			if (!IsCurrent(entry) || zdo.Created)
			{
				RemoveQueuedNearObjectAt(i);
				continue;
			}

			if (!ZoneSystem.instance.IsZoneReadyForType(zdo.GetSector(), zdo.Type))
			{
				continue;
			}

			if (scene.CreateObject(zdo) != null)
			{
				RemoveQueuedNearObjectAt(i);
				created++;
				if (created >= targetCount)
				{
					break;
				}
				continue;
			}

			if (ZNet.instance.IsServer())
			{
				RemoveQueuedNearObjectAt(i);
				zdo.SetOwner(ZDOMan.GetSessionID());
				ZLog.Log("Destroyed invalid prefab ZDO:" + entry.Id);
				ZDOMan.instance.DestroyZDO(zdo);
			}
		}
	}

	private static void SortNearObjects(Vector3 referencePosition)
	{
		SortNearObjectsMarker.Begin();
		try
		{
			for (var i = 0; i < QueuedNearObjects.Count; i++)
			{
				var zdo = QueuedNearObjects[i].ZDO;
				zdo.m_tempSortValue = Utils.DistanceSqr(referencePosition, zdo.GetPosition());
			}

			QueuedNearObjects.Sort(ReverseQueuedZdoComparer);
			QueuedNearObjectIndices.Clear();
			for (var i = 0; i < QueuedNearObjects.Count; i++)
			{
				QueuedNearObjectIndices.Add(QueuedNearObjects[i].Id, i);
			}

			_nearObjectsNeedSort = false;
		}
		finally
		{
			SortNearObjectsMarker.End();
		}
	}

	private static void CreateDistantObjects(
		ZNetScene scene, int maxCreatedPerFrame, ref int created)
	{
		if (created > maxCreatedPerFrame)
		{
			return;
		}

		for (var i = QueuedDistantObjects.Count - 1; i >= 0; i--)
		{
			var entry = QueuedDistantObjects[i];
			var zdo = entry.ZDO;
			if (!IsCurrent(entry) || zdo.Created)
			{
				QueuedDistantObjects.RemoveAtSwapBack(i, QueuedDistantObjectIndices, QueuedZdoIdSelector);
				continue;
			}

			if (scene.CreateObject(zdo) != null)
			{
				QueuedDistantObjects.RemoveAtSwapBack(i, QueuedDistantObjectIndices, QueuedZdoIdSelector);
				created++;
				if (created > maxCreatedPerFrame)
				{
					break;
				}
				continue;
			}

			if (ZNet.instance.IsServer())
			{
				QueuedDistantObjects.RemoveAtSwapBack(i, QueuedDistantObjectIndices, QueuedZdoIdSelector);
				zdo.SetOwner(ZDOMan.GetSessionID());
				ZLog.Log("Destroyed invalid predab ZDO:" + entry.Id + "  prefab hash:" + zdo.GetPrefab());
				ZDOMan.instance.DestroyZDO(zdo);
			}
		}
	}

	private static void DestroyQueuedObjects(ZNetScene scene)
	{
		DestroyQueuedObjectsMarker.Begin();
		try
		{
			foreach (var entry in PendingRemovalObjects.Values)
			{
				var zdo = entry.ZDO;
				if (!IsCurrent(entry) || GetAreaMembership(zdo) != AreaMembership.None)
				{
					continue;
				}

				if (!scene.m_instances.TryGetValue(zdo, out var zNetView))
				{
					continue;
				}

				zNetView.ResetZDO();
				Object.Destroy(zNetView.gameObject);
				if (!zdo.Persistent && zdo.IsOwner())
				{
					ZDOMan.instance.DestroyZDO(zdo);
				}
				scene.m_instances.Remove(zdo);
			}
		}
		finally
		{
			PendingRemovalObjects.Clear();
			DestroyQueuedObjectsMarker.End();
		}
	}

	private static void MarkAreaMembershipDirty(ZDOID id)
	{
		if (_cacheInitialized)
		{
			PendingMembershipIds.Add(id);
		}
	}

	private static void ResetObjectCache()
	{
		QueuedNearObjects.Clear();
		QueuedDistantObjects.Clear();
		PendingRemovalObjects.Clear();
		PendingMembershipIds.Clear();
		InvalidatedZdoIds.Clear();
		QueuedNearObjectIndices.Clear();
		QueuedDistantObjectIndices.Clear();
		ActiveNearZones.Clear();
		ActiveDistantZones.Clear();
		NextNearZones.Clear();
		NextDistantZones.Clear();
		ChangedZones.Clear();
		_nearObjectsNeedSort = false;
		_cacheInitialized = false;
	}

	private static void ApplyPendingZDOAreaUpdates()
	{
		ApplyPendingMembershipUpdatesMarker.Begin();
		foreach (var id in PendingMembershipIds)
		{
			var zdo = ZDOMan.instance.GetZDO(id);
			if (zdo != null)
			{
				UpdateZDOAreaMembership(zdo);
			}
		}
		PendingMembershipIds.Clear();
		ApplyPendingMembershipUpdatesMarker.End();
	}

	private static void UpdateZoneObjectMemberships(
		Vector2s sector, AreaMembership previousZoneMembership, AreaMembership nextZoneMembership)
	{
		var zdoMan = ZDOMan.instance;
		var sectorIndex = ZoneSystem.SectorToIndex(sector);
		List<ZDO> objects = zdoMan.m_objectsBySector[sectorIndex.Sector];
		if (objects != null)
		{
			for (var i = 0; i < objects.Count; i++)
			{
				var zdo = objects[i];
				if (previousZoneMembership != AreaMembership.Near
				    && nextZoneMembership != AreaMembership.Near
				    && !zdo.Distant)
				{
					continue;
				}

				var previousMembership = GetNonPortalMembership(zdo, previousZoneMembership);
				var nextMembership = GetNonPortalMembership(zdo, nextZoneMembership);
				TransitionZDOAreaMembership(zdo, previousMembership, nextMembership);
			}
		}

		// Portals are stored outside m_objectsBySector and only participate in the near area.
		var previousPortalMembership = GetPortalMembership(previousZoneMembership);
		var nextPortalMembership = GetPortalMembership(nextZoneMembership);
		if (previousPortalMembership == nextPortalMembership) { return; }
		if (!zdoMan.m_portalObjects.TryGetValue(sectorIndex, out List<ZDO> portals)) { return; }

		for (var i = 0; i < portals.Count; i++)
		{
			TransitionZDOAreaMembership(
				portals[i], previousPortalMembership, nextPortalMembership);
		}
	}

	private static void UpdateZDOAreaMembership(ZDO zdo)
	{
		var desiredMembership = GetAreaMembership(zdo);
		var id = zdo.m_uid;
		if (zdo.Created)
		{
			RemoveTrackedMembership(id);
			if (desiredMembership == AreaMembership.None)
			{
				PendingRemovalObjects[id] = new QueuedZDO(zdo);
			}
			else
			{
				PendingRemovalObjects.Remove(id);
			}
			return;
		}

		PendingRemovalObjects.Remove(id);
		if (desiredMembership == AreaMembership.Near)
		{
			RemoveQueuedObject(QueuedDistantObjects, QueuedDistantObjectIndices, id);
			if (AddQueuedObject(QueuedNearObjects, QueuedNearObjectIndices, zdo))
			{
				_nearObjectsNeedSort = true;
			}
		}
		else if (desiredMembership == AreaMembership.Distant)
		{
			if (RemoveQueuedObject(QueuedNearObjects, QueuedNearObjectIndices, id))
			{
				_nearObjectsNeedSort = true;
			}
			AddQueuedObject(QueuedDistantObjects, QueuedDistantObjectIndices, zdo);
		}
		else
		{
			RemoveTrackedMembership(id);
		}
	}

	private static void TransitionZDOAreaMembership(
		ZDO zdo, AreaMembership previousMembership, AreaMembership nextMembership)
	{
		if (previousMembership == nextMembership) { return; }

		var id = zdo.m_uid;
		if (zdo.Created)
		{
			if (nextMembership == AreaMembership.None)
			{
				PendingRemovalObjects[id] = new QueuedZDO(zdo);
			}
			else if (previousMembership == AreaMembership.None)
			{
				PendingRemovalObjects.Remove(id);
			}
			return;
		}

		if (previousMembership == AreaMembership.Near)
		{
			if (RemoveQueuedObject(QueuedNearObjects, QueuedNearObjectIndices, id))
			{
				_nearObjectsNeedSort = true;
			}
		}
		else if (previousMembership == AreaMembership.Distant)
		{
			RemoveQueuedObject(QueuedDistantObjects, QueuedDistantObjectIndices, id);
		}

		if (nextMembership == AreaMembership.Near)
		{
			if (AddQueuedObject(QueuedNearObjects, QueuedNearObjectIndices, zdo))
			{
				_nearObjectsNeedSort = true;
			}
		}
		else if (nextMembership == AreaMembership.Distant)
		{
			AddQueuedObject(QueuedDistantObjects, QueuedDistantObjectIndices, zdo);
		}
	}

	private static AreaMembership GetZoneMembership(
		Vector2s sector, HashSet<Vector2s> nearZones, HashSet<Vector2s> distantZones)
	{
		if (nearZones.Contains(sector)) { return AreaMembership.Near; }
		return distantZones.Contains(sector) ? AreaMembership.Distant : AreaMembership.None;
	}

	private static AreaMembership GetNonPortalMembership(
		ZDO zdo, AreaMembership zoneMembership)
	{
		return zoneMembership != AreaMembership.Distant || zdo.Distant
			? zoneMembership
			: AreaMembership.None;
	}

	private static AreaMembership GetPortalMembership(AreaMembership zoneMembership)
	{
		return zoneMembership == AreaMembership.Near ? AreaMembership.Near : AreaMembership.None;
	}

	private static AreaMembership GetAreaMembership(ZDO zdo)
	{
		if (InvalidatedZdoIds.Contains(zdo.m_uid)) { return AreaMembership.None; }

		var sector = zdo.GetSector();
		if (ActiveNearZones.Contains(sector)) { return AreaMembership.Near; }

		return zdo.Distant && !IsPortal(zdo) && ActiveDistantZones.Contains(sector)
			? AreaMembership.Distant
			: AreaMembership.None;
	}

	private static bool IsPortal(ZDO zdo)
	{
		return Game.instance.PortalPrefabHash.Contains(zdo.GetPrefab());
	}

	private static void RemoveTrackedMembership(ZDOID id)
	{
		if (RemoveQueuedObject(QueuedNearObjects, QueuedNearObjectIndices, id))
		{
			_nearObjectsNeedSort = true;
		}
		RemoveQueuedObject(QueuedDistantObjects, QueuedDistantObjectIndices, id);
	}

	private static bool AddQueuedObject(
		List<QueuedZDO> queue, Dictionary<ZDOID, int> indices, ZDO zdo)
	{
		if (indices.TryGetValue(zdo.m_uid, out var index))
		{
			if (ReferenceEquals(queue[index].ZDO, zdo)) { return false; }

			queue[index] = new QueuedZDO(zdo);
			return true;
		}

		indices.Add(zdo.m_uid, queue.Count);
		queue.Add(new QueuedZDO(zdo));
		return true;
	}

	private static bool RemoveQueuedObject(
		List<QueuedZDO> queue, Dictionary<ZDOID, int> indices, ZDOID id)
	{
		if (!indices.TryGetValue(id, out var index)) { return false; }

		queue.RemoveAtSwapBack(index, indices, QueuedZdoIdSelector);
		return true;
	}

	private static void RemoveQueuedNearObjectAt(int index)
	{
		var movedObject = index != QueuedNearObjects.Count - 1;
		QueuedNearObjects.RemoveAtSwapBack(index, QueuedNearObjectIndices, QueuedZdoIdSelector);
		if (movedObject)
		{
			_nearObjectsNeedSort = true;
		}
	}

	private static bool IsCurrent(QueuedZDO entry)
	{
		return ReferenceEquals(ZDOMan.instance.GetZDO(entry.Id), entry.ZDO);
	}

	private static int CompareQueuedZdos(QueuedZDO x, QueuedZDO y)
	{
		if (x.ZDO.Type == y.ZDO.Type)
		{
			return Utils.CompareFloats(x.ZDO.m_tempSortValue, y.ZDO.m_tempSortValue);
		}

		return ((int)y.ZDO.Type).CompareTo((int)x.ZDO.Type);
	}

	private static int ReverseQueuedZdoComparer(QueuedZDO x, QueuedZDO y)
	{
		return CompareQueuedZdos(y, x);
	}
}
