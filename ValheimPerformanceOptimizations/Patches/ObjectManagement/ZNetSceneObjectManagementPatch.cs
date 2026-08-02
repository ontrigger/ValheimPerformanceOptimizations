using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;
using ValheimPerformanceOptimizations.Storage;
using Object = UnityEngine.Object;

namespace ValheimPerformanceOptimizations.Patches.ObjectManagement
{
	[HarmonyPatch]
	public static partial class ZNetSceneObjectManagementPatch
	{
		private static Vector2s _currentZone = new(short.MinValue, short.MinValue);
		private const float NearQueueResortDistance = 8f;
		private const float NearQueueResortDistanceSqr = NearQueueResortDistance * NearQueueResortDistance;
		private static Vector3 _lastNearQueueSortPosition;
		private static bool _nearQueueSortDirty = true;

		private static HashSet<Vector2s> _lastNearZoneSet = new();
		private static HashSet<Vector2s> _lastDistantZoneSet = new();

		private static readonly List<ZDO> QueuedNearObjects = new();
		private static readonly List<ZDO> QueuedDistantObjects = new();
		private static readonly Dictionary<ZDO, int> QueuedNearObjectIndices = new();
		private static readonly Dictionary<ZDO, int> QueuedDistantObjectIndices = new();
		private static readonly HashSet<ZDO> PendingRpcZdoQueue = new();
		private static bool _processingRpcZdoData;

		private static readonly List<ZDO> RemoveQueue = new();
		private static readonly Dictionary<ZDO, int> RemoveQueueIndices = new();
		private static readonly Comparison<ZDO> ReverseZDOComparer = ReverseZDOCompare;

		[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.CreateDestroyObjects))] [HarmonyPrefix]
		public static bool ZNetScene_CreateDestroyObjects_Prefix(ZNetScene __instance)
		{
			var refZoneInt = ZoneSystem.GetZone(ZNet.instance.GetReferencePosition());
			var refZone = refZoneInt.ClampToShort();
			__instance.m_tempCurrentObjects.Clear();
			__instance.m_tempCurrentDistantObjects.Clear();

			Profiler.BeginSample("my cool logic");
			if (refZone != _currentZone)
			{
				Profiler.BeginSample("get zone set");
				HashSet<Vector2s> nearZones = SetPool<Vector2s>.Get();
				HashSet<Vector2s> distantZones = SetPool<Vector2s>.Get();

				GetActiveZoneSet(refZone, nearZones, distantZones);
				Profiler.EndSample();

				Profiler.BeginSample("near");
				var nearChanged = false;
				foreach (var zone in nearZones)
				{
					if (_lastNearZoneSet.Contains(zone)) { continue; }

					CollectZoneObjects(zone, QueuedNearObjects, QueuedNearObjectIndices, false);
					nearChanged = true;
				}

				foreach (var zone in _lastNearZoneSet)
				{
					if (nearZones.Contains(zone)) { continue; }

					UnloadZoneObjects(zone, QueuedNearObjects, QueuedNearObjectIndices, false);
					nearChanged = true;
				}

				if (nearChanged)
				{
					_nearQueueSortDirty = true;
				}
				Profiler.EndSample();

				Profiler.BeginSample("far");
				foreach (var zone in distantZones)
				{
					if (_lastDistantZoneSet.Contains(zone)) { continue; }

					CollectZoneObjects(zone, QueuedDistantObjects, QueuedDistantObjectIndices, true);
				}

				foreach (var zone in _lastDistantZoneSet)
				{
					if (distantZones.Contains(zone)) { continue; }

					UnloadZoneObjects(zone, QueuedDistantObjects, QueuedDistantObjectIndices, true);
				}

				SetPool<Vector2s>.Return(_lastNearZoneSet);
				SetPool<Vector2s>.Return(_lastDistantZoneSet);

				_lastNearZoneSet = nearZones;
				_lastDistantZoneSet = distantZones;
				Profiler.EndSample();
			}

			Profiler.EndSample();
			_currentZone = refZone;

			Profiler.BeginSample("vanilla");
			__instance.CreateObjects(__instance.m_tempCurrentObjects, __instance.m_tempCurrentDistantObjects);
			__instance.RemoveObjects(__instance.m_tempCurrentObjects, __instance.m_tempCurrentDistantObjects);
			Profiler.EndSample();

			return false;
		}

		[HarmonyPrefix] [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.CreateObjects))]
		private static bool ZNetScene_CreateObjects_Prefix(
			ZNetScene __instance, List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)
		{
			var maxCreatedPerFrame = 10;
			if (__instance.InLoadingScreen())
			{
				maxCreatedPerFrame = 100;
			}

			var created = 0;
			__instance.CreateObjectsSorted(currentNearObjects, maxCreatedPerFrame, ref created);
			var distantCreated = 0;
			__instance.CreateDistantObjects(currentDistantObjects, maxCreatedPerFrame, ref distantCreated);

			return false;
		}

		[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.CreateObjectsSorted))] [HarmonyPrefix]
		private static bool CreateObjectsSorted(
			ZNetScene __instance, List<ZDO> currentNearObjects,
			int maxCreatedPerFrame, ref int created)
		{
			if (!ZoneSystem.instance.IsActiveAreaLoaded())
			{
				return false;
			}

			__instance.m_tempCurrentObjects2.Clear();
			var refPos = ZNet.instance.GetReferencePosition();

			var num = Mathf.Max(QueuedNearObjects.Count / 100, maxCreatedPerFrame);
			if (_nearQueueSortDirty
			    || Utils.DistanceSqr(_lastNearQueueSortPosition, refPos) >= NearQueueResortDistanceSqr)
			{
				Profiler.BeginSample("sortin");
				for (var i = 0; i < QueuedNearObjects.Count; i++)
				{
					var queuedNearObject = QueuedNearObjects[i];
					queuedNearObject.m_tempSortValue =
						Utils.DistanceSqr(refPos, queuedNearObject.GetPosition());
				}

				QueuedNearObjects.Sort(ReverseZDOComparer);
				QueuedNearObjectIndices.Clear();
				for (var i = 0; i < QueuedNearObjects.Count; i++)
				{
					QueuedNearObjectIndices.Add(QueuedNearObjects[i], i);
				}
				_lastNearQueueSortPosition = refPos;
				_nearQueueSortDirty = false;
				Profiler.EndSample();
			}

			Profiler.BeginSample("spawnin 1");
			for (var i = QueuedNearObjects.Count - 1; i >= 0; i--)
			{
				var zdo = QueuedNearObjects[i];
				if (!QueuedNearObjectIndices.ContainsKey(zdo)
				    || __instance.m_instances.ContainsKey(zdo)
				    || !IsInActiveRange(zdo.Distant, zdo.m_sector))
				{
					RemoveQueuedAtPreservingOrder(QueuedNearObjects, QueuedNearObjectIndices, i);
					continue;
				}

				if (!ZoneSystem.instance.IsZoneReadyForType(zdo.GetSector(), zdo.Type))
				{
					continue;
				}

				if (__instance.CreateObject(zdo) != null)
				{
					RemoveQueuedAtPreservingOrder(QueuedNearObjects, QueuedNearObjectIndices, i);
					created++;
					if (created > num) { break; }
				}
				else if (ZNet.instance.IsServer())
				{
					zdo.SetOwner(ZDOMan.GetSessionID());
					var uid = zdo.m_uid;
					ZLog.Log("Destroyed invalid predab ZDO:" + uid);
					ZDOMan.instance.DestroyZDO(zdo);
				}
			}
			Profiler.EndSample();

			return false;
		}

		[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.CreateDistantObjects))] [HarmonyPrefix]
		private static bool CreateDistantObjects(
			ZNetScene __instance, List<ZDO> objects, int maxCreatedPerFrame,
			ref int created)
		{
			if (created > maxCreatedPerFrame) { return false; }

			Profiler.BeginSample("spawnin 2");
			for (var i = QueuedDistantObjects.Count - 1; i >= 0; i--)
			{
				var zdo = QueuedDistantObjects[i];
				if (!QueuedDistantObjectIndices.ContainsKey(zdo)
				    || __instance.m_instances.ContainsKey(zdo)
				    || !IsInActiveRange(zdo.Distant, zdo.m_sector))
				{
					RemoveQueuedAt(QueuedDistantObjects, QueuedDistantObjectIndices, i);
					continue;
				}

				if (__instance.CreateObject(zdo) != null)
				{
					RemoveQueuedAt(QueuedDistantObjects, QueuedDistantObjectIndices, i);
					created++;
					if (created > maxCreatedPerFrame) { break; }
				}
				else if (ZNet.instance.IsServer())
				{
					zdo.SetOwner(ZDOMan.GetSessionID());
					var uid = zdo.m_uid;
					ZLog.Log("Destroyed invalid predab ZDO:" + uid + "  prefab hash:" + zdo.GetPrefab());
					ZDOMan.instance.DestroyZDO(zdo);
				}
			}
			Profiler.EndSample();

			return false;
		}

		[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.RemoveObjects))] [HarmonyPrefix]
		private static bool RemoveObjects(
			ZNetScene __instance, List<ZDO> currentNearObjects,
			List<ZDO> currentDistantObjects)
		{
			__instance.m_tempRemoved.Clear();

			Profiler.BeginSample("removin");
			for (var i = 0; i < RemoveQueue.Count; i++)
			{
				var zdo = RemoveQueue[i];
				if (IsInActiveRange(zdo.Distant, zdo.m_sector)) { continue; }

				if (!__instance.m_instances.TryGetValue(zdo, out var zNetView))
				{
					// this object was either removed by ZNetScene.Destroy or hasn't even spawned yet
					continue;
				}
				zNetView.ResetZDO();
				Object.Destroy(zNetView.gameObject);
				if (!zdo.Persistent && zdo.IsOwner())
				{
					ZDOMan.instance.DestroyZDO(zdo);
				}
				__instance.m_instances.Remove(zdo);
			}
			Profiler.EndSample();

			RemoveQueue.Clear();
			RemoveQueueIndices.Clear();

			return false;
		}

		[HarmonyPrefix] [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData")]
		private static void ZDOMan_RPC_ZDOData_Prefix()
		{
			_processingRpcZdoData = true;
			PendingRpcZdoQueue.Clear();
		}

		[HarmonyPostfix] [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData")]
		private static void ZDOMan_RPC_ZDOData_Postfix()
		{
			_processingRpcZdoData = false;
			foreach (var zdo in PendingRpcZdoQueue)
			{
				if (!ZNetScene.instance.HaveInstance(zdo)
				    && IsInActiveRange(zdo.Distant, zdo.m_sector))
				{
					EnqueueForCreation(zdo);
				}
			}
			PendingRpcZdoQueue.Clear();
		}

		[HarmonyPostfix] [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.AddToSector))]
		public static void ZDOMan_AddToSector_Postfix(ZDOMan __instance, ZDO zdo, Vector2i sector)
		{
			if (ZNetScene.instance.HaveInstance(zdo)) { return; }

			if (_processingRpcZdoData)
			{
				PendingRpcZdoQueue.Add(zdo);
				return;
			}

			var sectorShort = sector.ClampToShort();
			if (zdo.Distant)
			{
				if (_lastDistantZoneSet.Contains(sectorShort))
				{
					EnqueueForCreation(zdo);
				}
			}
			else if (_lastNearZoneSet.Contains(sectorShort))
			{
				EnqueueForCreation(zdo);
			}
		}

		[HarmonyPostfix] [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.RemoveFromSector))]
		public static void ZDOMan_RemoveFromSector_Postfix(ZDOMan __instance, ZDO zdo, Vector2i sector)
		{
			if (zdo.Distant)
			{
				RemoveQueuedObject(QueuedDistantObjects, QueuedDistantObjectIndices, zdo);
				return;
			}

			RemoveQueuedObject(QueuedNearObjects, QueuedNearObjectIndices, zdo);
			_nearQueueSortDirty = true;
		}

		[HarmonyPrefix] [HarmonyPatch(typeof(ZDO), nameof(ZDO.SetSector))]
		private static bool ZDO_SetSector_Prefix(ZDO __instance, Vector2i sector)
		{
			if (__instance.m_sector == sector) { return true; }

			var newSector = sector.ClampToShort();
			if (ZNetScene.instance.HaveInstance(__instance)
			                  && !IsInActiveRange(__instance.Distant, newSector))
			{
				AddUnique(RemoveQueue, RemoveQueueIndices, __instance);
			}

			return true;
		}

		[HarmonyPrefix] [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Shutdown))]
		private static bool ZNetScene_Shutdown_Prefix(ZNetScene __instance)
		{
			QueuedNearObjects.Clear();
			QueuedDistantObjects.Clear();
			QueuedNearObjectIndices.Clear();
			QueuedDistantObjectIndices.Clear();
			PendingRpcZdoQueue.Clear();
			_processingRpcZdoData = false;

			_lastNearZoneSet.Clear();
			_lastDistantZoneSet.Clear();

			RemoveQueue.Clear();
			RemoveQueueIndices.Clear();

			_currentZone = new Vector2s(short.MinValue, short.MinValue);
			_nearQueueSortDirty = true;

			return true;
		}

		private static int ReverseZDOCompare(ZDO x, ZDO y)
		{
			if (x.Type == y.Type)
			{
				return y.m_tempSortValue.CompareTo(x.m_tempSortValue);
			}

			if (x.Type < y.Type)
			{
				return -1;
			}
			return 1;
		}

		private static bool IsInActiveRange(bool distant, Vector2s sector)
		{
			if (_lastNearZoneSet.Contains(sector)) { return true; }

			return distant && _lastDistantZoneSet.Contains(sector);
		}

		private static void EnqueueForCreation(ZDO zdo)
		{
			if (zdo.Distant)
			{
				AddUnique(QueuedDistantObjects, QueuedDistantObjectIndices, zdo);
				return;
			}

			AddUnique(QueuedNearObjects, QueuedNearObjectIndices, zdo);
			_nearQueueSortDirty = true;
		}

		private static void AddUnique(List<ZDO> queue, Dictionary<ZDO, int> queueIndices, ZDO zdo)
		{
			if (!queueIndices.ContainsKey(zdo))
			{
				queueIndices.Add(zdo, queue.Count);
				queue.Add(zdo);
			}
		}

		private static void RemoveQueuedObject(
			List<ZDO> queue, Dictionary<ZDO, int> queueIndices, ZDO zdo)
		{
			if (queueIndices.TryGetValue(zdo, out var index))
			{
				RemoveQueuedAt(queue, queueIndices, index);
			}
		}

		private static void RemoveQueuedAt(
			List<ZDO> queue, Dictionary<ZDO, int> queueIndices, int index)
		{
			var lastIndex = queue.Count - 1;
			var removed = queue[index];
			queueIndices.Remove(removed);
			if (index != lastIndex)
			{
				var moved = queue[lastIndex];
				queue[index] = moved;
				queueIndices[moved] = index;
			}
			queue.RemoveAt(lastIndex);
		}

		private static void RemoveQueuedAtPreservingOrder(
			List<ZDO> queue, Dictionary<ZDO, int> queueIndices, int index)
		{
			queueIndices.Remove(queue[index]);
			queue.RemoveAt(index);
			for (var i = index; i < queue.Count; i++)
			{
				queueIndices[queue[i]] = i;
			}
		}
	}
}
