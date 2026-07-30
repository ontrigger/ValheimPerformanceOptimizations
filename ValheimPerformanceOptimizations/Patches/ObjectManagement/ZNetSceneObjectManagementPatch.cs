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

		private static HashSet<Vector2s> _lastNearZoneSet = new();
		private static HashSet<Vector2s> _lastDistantZoneSet = new();

		private static readonly List<ZDO> QueuedNearObjects = new();
		private static readonly List<ZDO> QueuedDistantObjects = new();
		private static readonly Dictionary<ZDO, int> QueuedNearObjectIndices = new();
		private static readonly Dictionary<ZDO, int> QueuedDistantObjectIndices = new();

		private static readonly List<ZDO> RemoveQueue = new();
		private static readonly Dictionary<ZDO, int> RemoveQueueIndices = new();

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

				HashSet<Vector2s> nearZonesToLoad = SetPool<Vector2s>.Get();
				foreach (var zone in nearZones)
				{
					if (!_lastNearZoneSet.Contains(zone))
					{
						nearZonesToLoad.Add(zone);
					}
				}

				HashSet<Vector2s> nearZonesToUnload = SetPool<Vector2s>.Get();
				foreach (var zone in _lastNearZoneSet)
				{
					if (!nearZones.Contains(zone))
					{
						nearZonesToUnload.Add(zone);
					}
				}

				Profiler.BeginSample("remove all");
				RemoveQueuedObjectsInZones(QueuedNearObjects, QueuedNearObjectIndices, nearZonesToUnload);
				Profiler.EndSample();

				foreach (var zone in nearZonesToUnload)
				{
					CollectNearZoneObjects(zone, RemoveQueue, RemoveQueueIndices);
				}

				foreach (var zone in nearZonesToLoad)
				{
					CollectNearZoneObjects(zone, QueuedNearObjects, QueuedNearObjectIndices);
				}

				SetPool<Vector2s>.Return(nearZonesToLoad);
				SetPool<Vector2s>.Return(nearZonesToUnload);

				Profiler.EndSample();

				Profiler.BeginSample("far");

				HashSet<Vector2s> distantZonesToLoad = SetPool<Vector2s>.Get();
				foreach (var zone in distantZones)
				{
					if (!_lastDistantZoneSet.Contains(zone))
					{
						distantZonesToLoad.Add(zone);
					}
				}

				HashSet<Vector2s> distantZonesToUnload = SetPool<Vector2s>.Get();
				foreach (var zone in _lastDistantZoneSet)
				{
					if (!distantZones.Contains(zone))
					{
						distantZonesToUnload.Add(zone);
					}
				}

				RemoveQueuedObjectsInZones(QueuedDistantObjects, QueuedDistantObjectIndices, distantZonesToUnload);
				foreach (var zone in distantZonesToUnload)
				{
					ZNetSceneObjectManagementPatch.CollectDistantZoneObjects(zone, RemoveQueue, RemoveQueueIndices);
				}

				foreach (var zone in distantZonesToLoad)
				{
					ZNetSceneObjectManagementPatch.CollectDistantZoneObjects(zone, QueuedDistantObjects, QueuedDistantObjectIndices);
				}

				SetPool<Vector2s>.Return(distantZonesToLoad);
				SetPool<Vector2s>.Return(distantZonesToUnload);

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
			__instance.CreateDistantObjects(currentDistantObjects, maxCreatedPerFrame, ref created);

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
			Profiler.BeginSample("sortin");
			foreach (var currentNearObject in QueuedNearObjects)
			{
				currentNearObject.m_tempSortValue = Utils.DistanceSqr(refPos, currentNearObject.GetPosition());
			}

			QueuedNearObjects.Sort(ReverseZDOCompare);
			RebuildQueueIndices(QueuedNearObjects, QueuedNearObjectIndices);
			Profiler.EndSample();

			Profiler.BeginSample("spawnin 1");
			for (var i = QueuedNearObjects.Count - 1; i >= 0; i--)
			{
				var zdo = QueuedNearObjects[i];
				if (!QueuedNearObjectIndices.ContainsKey(zdo)
				    || __instance.m_instances.ContainsKey(zdo)
				    || !IsInActiveRange(zdo.Distant, zdo.m_sector))
				{
					RemoveQueuedAt(QueuedNearObjects, QueuedNearObjectIndices, i);
					continue;
				}

				if (!ZoneSystem.instance.IsZoneReadyForType(zdo.GetSector(), zdo.Type))
				{
					continue;
				}

				if (__instance.CreateObject(zdo) != null)
				{
					RemoveQueuedObject(QueuedNearObjects, QueuedNearObjectIndices, zdo);
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
					RemoveQueuedObject(QueuedDistantObjects, QueuedDistantObjectIndices, zdo);
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

		[HarmonyPostfix] [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.AddToSector))]
		public static void ZDOMan_AddToSector_Postfix(ZDOMan __instance, ZDO zdo, Vector2i sector)
		{
			if (ZNetScene.instance.HaveInstance(zdo)) { return; }

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
			RemoveFromCreationQueue(zdo);
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

			_lastNearZoneSet.Clear();
			_lastDistantZoneSet.Clear();

			RemoveQueue.Clear();
			RemoveQueueIndices.Clear();

			_currentZone = new Vector2s(short.MinValue, short.MinValue);

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
			return distant
				? _lastDistantZoneSet.Contains(sector)
				: _lastNearZoneSet.Contains(sector);
		}

		private static void EnqueueForCreation(ZDO zdo)
		{
			if (zdo.Distant)
			{
				AddUnique(QueuedDistantObjects, QueuedDistantObjectIndices, zdo);
				return;
			}

			AddUnique(QueuedNearObjects, QueuedNearObjectIndices, zdo);
		}

		private static void RemoveFromCreationQueue(ZDO zdo)
		{
			if (zdo.Distant)
			{
				RemoveQueuedObject(QueuedDistantObjects, QueuedDistantObjectIndices, zdo);
				return;
			}

			RemoveQueuedObject(QueuedNearObjects, QueuedNearObjectIndices, zdo);
		}

		private static void AddUnique(List<ZDO> queue, Dictionary<ZDO, int> queueIndices, ZDO zdo)
		{
			if (!queueIndices.ContainsKey(zdo))
			{
				queueIndices.Add(zdo, queue.Count);
				queue.Add(zdo);
			}
		}

		private static void RemoveQueuedObjectsInZones(
			List<ZDO> queue, Dictionary<ZDO, int> queueIndices, HashSet<Vector2s> zones)
		{
			for (var i = queue.Count - 1; i >= 0; i--)
			{
				var zdo = queue[i];
				if (!zones.Contains(zdo.m_sector)) { continue; }

				RemoveQueuedAt(queue, queueIndices, i);
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

		private static void RebuildQueueIndices(List<ZDO> queue, Dictionary<ZDO, int> queueIndices)
		{
			queueIndices.Clear();
			for (var i = 0; i < queue.Count; i++)
			{
				queueIndices.Add(queue[i], i);
			}
		}
	}
}
