using System;
using System.Collections.Generic;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Profiling;
using ValheimPerformanceOptimizations.Storage;
using Object = UnityEngine.Object;

namespace ValheimPerformanceOptimizations.Patches
{
	using VPO = ValheimPerformanceOptimizations;

	[HarmonyPatch]
	public static class ZNetSceneObjectManagementPatch
	{
		public static bool CreateRemoveHack;

		private static Vector2i _currentZone = new(Int32.MinValue, Int32.MinValue);

		private static HashSet<Vector2i> _lastNearZoneSet = new();
		private static HashSet<Vector2i> _lastDistantZoneSet = new();

		private static readonly Dictionary<Vector2i, List<ZDO>> QueuedNearObjectsByZone = new();
		private static readonly Dictionary<Vector2i, List<ZDO>> QueuedDistantObjectsByZone = new();

		private static readonly List<ZDO> QueuedNearObjects = new();
		private static readonly List<ZDO> QueuedDistantObjects = new();

		// dynamic objects that were spawned outside the CreateDestroy loop such as NPCs, vfx/sfx and so on
		private static readonly List<ZNetView> ExternallySpawnedObjects = new();

		private static readonly List<ZDO> RemoveQueue = new();

		private static readonly Predicate<ZDO> GetDistant = zdo => !zdo.m_distant;
		private static readonly Predicate<ZDO> GetNear = zdo => zdo.m_distant;

		[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.CreateDestroyObjects))] [HarmonyPrefix]
		public static bool ZNetScene_CreateDestroyObjects_Prefix(ZNetScene __instance)
		{
			var refZone = ZoneSystem.instance.GetZone(ZNet.instance.GetReferencePosition());
			__instance.m_tempCurrentObjects.Clear();
			__instance.m_tempCurrentDistantObjects.Clear();

			Profiler.BeginSample("my cool logic");
			if (refZone != _currentZone)
			{
				//VPO.Logger.LogInfo($"Changed zone from {_currentZone} to {refZone}");

				Profiler.BeginSample("get zone set");
				HashSet<Vector2i> nearZones = SetPool<Vector2i>.Get();
				HashSet<Vector2i> distantZones = SetPool<Vector2i>.Get();

				GetActiveZoneSet(refZone, nearZones, distantZones);
				Profiler.EndSample();

				Profiler.BeginSample("near");

				HashSet<Vector2i> nearZonesToLoad = SetPool<Vector2i>.Get();
				foreach (var zone in nearZones)
				{
					if (!_lastNearZoneSet.Contains(zone))
					{
						nearZonesToLoad.Add(zone);
					}
				}

				HashSet<Vector2i> nearZonesToUnload = SetPool<Vector2i>.Get();
				foreach (var zone in _lastNearZoneSet)
				{
					if (!nearZones.Contains(zone))
					{
						nearZonesToUnload.Add(zone);
					}
				}

				Profiler.BeginSample("remove all");
				QueuedNearObjects.RemoveAll(zdo => nearZonesToUnload.Contains(zdo.m_sector));
				Profiler.EndSample();

				foreach (var zone in nearZonesToUnload)
				{
					CollectZoneObjects(zone, RemoveQueue, GetDistant);
				}

				foreach (var zone in nearZonesToLoad)
				{
					CollectZoneObjects(zone, QueuedNearObjects, GetDistant);
				}

				SetPool<Vector2i>.Return(nearZonesToLoad);
				SetPool<Vector2i>.Return(nearZonesToUnload);

				Profiler.EndSample();

				Profiler.BeginSample("far");

				HashSet<Vector2i> distantZonesToLoad = SetPool<Vector2i>.Get();
				foreach (var zone in distantZones)
				{
					if (!_lastDistantZoneSet.Contains(zone))
					{
						distantZonesToLoad.Add(zone);
					}
				}

				HashSet<Vector2i> distantZonesToUnload = SetPool<Vector2i>.Get();
				foreach (var zone in _lastDistantZoneSet)
				{
					if (!distantZones.Contains(zone))
					{
						distantZonesToUnload.Add(zone);
					}
				}

				QueuedDistantObjects.RemoveAll(zdo => distantZonesToUnload.Contains(zdo.m_sector));
				foreach (var zone in distantZonesToUnload)
				{
					CollectZoneObjects(zone, RemoveQueue, GetNear);
				}

				foreach (var zone in distantZonesToLoad)
				{
					CollectZoneObjects(zone, QueuedDistantObjects, GetNear);
				}

				SetPool<Vector2i>.Return(distantZonesToLoad);
				SetPool<Vector2i>.Return(distantZonesToUnload);

				SetPool<Vector2i>.Return(_lastNearZoneSet);
				SetPool<Vector2i>.Return(_lastDistantZoneSet);

				_lastNearZoneSet = nearZones;
				_lastDistantZoneSet = distantZones;

				Profiler.EndSample();
			}

			Profiler.EndSample();
			_currentZone = refZone;

			Profiler.BeginSample("vanilla");
			CreateRemoveHack = true;
			__instance.CreateObjects(__instance.m_tempCurrentObjects, __instance.m_tempCurrentDistantObjects);
			__instance.RemoveObjects(__instance.m_tempCurrentObjects, __instance.m_tempCurrentDistantObjects);
			CreateRemoveHack = false;
			Profiler.EndSample();

			return false;
		}

		private static void GetActiveZoneSet(
			Vector2i zone, HashSet<Vector2i> nearSectors, HashSet<Vector2i> distantSectors)
		{
			var nearArea = ZoneSystem.instance.m_activeArea;

			nearSectors.Add(zone);
			distantSectors.Add(zone);
			for (var i = 1; i <= nearArea; i++)
			{
				for (var j = zone.x - i; j <= zone.x + i; j++)
				{
					nearSectors.Add(new Vector2i(j, zone.y - i));
					nearSectors.Add(new Vector2i(j, zone.y + i));

					distantSectors.Add(new Vector2i(j, zone.y - i));
					distantSectors.Add(new Vector2i(j, zone.y + i));
				}
				for (var k = zone.y - i + 1; k <= zone.y + i - 1; k++)
				{
					nearSectors.Add(new Vector2i(zone.x - i, k));
					nearSectors.Add(new Vector2i(zone.x + i, k));

					distantSectors.Add(new Vector2i(zone.x - i, k));
					distantSectors.Add(new Vector2i(zone.x + i, k));
				}
			}

			var distantArea = ZoneSystem.instance.m_activeDistantArea;
			for (var l = nearArea + 1; l <= nearArea + distantArea; l++)
			{
				for (var m = zone.x - l; m <= zone.x + l; m++)
				{
					distantSectors.Add(new Vector2i(m, zone.y - l));
					distantSectors.Add(new Vector2i(m, zone.y + l));
				}

				for (var n = zone.y - l + 1; n <= zone.y + l - 1; n++)
				{
					distantSectors.Add(new Vector2i(zone.x - l, n));
					distantSectors.Add(new Vector2i(zone.x + l, n));
				}
			}
		}

		private static void CollectZoneObjects(Vector2i sector, ICollection<ZDO> objects, Predicate<ZDO> predicate)
		{
			var instance = ZDOMan.instance;
			var num = instance.SectorToIndex(sector);

			if (num >= 0)
			{
				List<ZDO> sectorObjects = instance.m_objectsBySector[num];
				if (sectorObjects == null) { return; }

				for (var i = 0; i < sectorObjects.Count; i++)
				{
					if (predicate(sectorObjects[i]))
					{
						objects.Add(sectorObjects[i]);
					}
				}
			}
			else
			{
				if (!instance.m_objectsByOutsideSector.TryGetValue(sector, out List<ZDO> sectorObjects))
				{
					return;
				}

				for (var j = 0; j < sectorObjects.Count; j++)
				{
					if (predicate(sectorObjects[j]))
					{
						objects.Add(sectorObjects[j]);
					}
				}
			}

		}
		
		[HarmonyPrefix, HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.CreateObjects))] 
		private static bool ZNetScene_CreateObjects_Prefix(ZNetScene __instance, List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)
		{
			int maxCreatedPerFrame = 10;
			if (__instance.InLoadingScreen())
			{
				maxCreatedPerFrame = 100;
			}
			
			int created = 0;
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
			Profiler.EndSample();

			Profiler.BeginSample("spawnin 1");
			for (var i = QueuedNearObjects.Count - 1; i >= 0; i--)
			{
				var zdo = QueuedNearObjects[i];

				if (__instance.CreateObject(zdo) != null)
				{
					QueuedNearObjects.RemoveAt(i);
					created++;
					if (created > num) { break; }
				}
				else if (ZNet.instance.IsServer())
				{
					zdo.SetOwner(ZDOMan.instance.GetMyID());
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
				if (__instance.CreateObject(zdo) != null)
				{
					QueuedDistantObjects.RemoveAt(i);
					created++;
					if (created > maxCreatedPerFrame) { break; }
				}
				else if (ZNet.instance.IsServer())
				{
					zdo.SetOwner(ZDOMan.instance.GetMyID());
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
				if (!__instance.m_instances.TryGetValue(zdo, out var zNetView))
				{
					// this object was either removed by ZNetScene.Destroy or hasn't even spawned yet
					continue;
				}
				zNetView.ResetZDO();
				Object.Destroy(zNetView.gameObject);
				if (!zdo.m_persistent && zdo.IsOwner())
				{
					ZDOMan.instance.DestroyZDO(zdo);
				}
				__instance.m_instances.Remove(zdo);
			}
			Profiler.EndSample();

			RemoveQueue.Clear();

			Profiler.BeginSample("removin 2");
			for (var i = ExternallySpawnedObjects.Count - 1; i >= 0; i--)
			{
				var netView = ExternallySpawnedObjects[i];
				// this makes no fucking sense, zdo should be reset after this method
				if (netView.GetZDO() == null || netView.GetZDO().GetPrefab() == 0)
				{
					ExternallySpawnedObjects.RemoveAtSwapBack(i);
					continue;
				}

				if (netView.m_distant && !_lastDistantZoneSet.Contains(netView.GetZDO().m_sector))
				{
					VPO.Logger.LogInfo("Removed external distant obj " + netView);
					ExternallySpawnedObjects.RemoveAtSwapBack(i);
				}
				else if (!netView.m_distant && !_lastNearZoneSet.Contains(netView.GetZDO().m_sector))
				{
					VPO.Logger.LogInfo("Removed external near obj " + netView);
					ExternallySpawnedObjects.RemoveAtSwapBack(i);
				}
			}
			Profiler.EndSample();

			return false;
		}

		[HarmonyPostfix] [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.AddInstance))]
		public static void AddInstance_Postfix(ZNetScene __instance, ZDO zdo, ZNetView nview)
		{
			// instance spawned outside main loop
			if (!CreateRemoveHack)
			{
				ExternallySpawnedObjects.Add(nview);
			}
		}

		/*[HarmonyPatch(typeof(ZDO), nameof(ZDO.SetSector))] [HarmonyPrefix]
		private static bool ZDO_SetSector_Prefix(ZDO __instance, Vector2i sector)
		{
			if (!(__instance.m_sector == sector))
			{
				__instance.m_zdoMan.RemoveFromSector(__instance, __instance.m_sector);
				__instance.m_sector = sector;
				__instance.m_zdoMan.AddToSector(__instance, __instance.m_sector);
				if (ZNet.instance.IsServer())
				{
					__instance.m_zdoMan.ZDOSectorInvalidated(__instance);
				}
			}

			return false;
		}*/

		[HarmonyPostfix] [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.OnZDODestroyed))]
		private static void OnZDODestroyed_Postfix(ZNetScene __instance, ZDO zdo)
		{
			//VPO.Logger.LogInfo("ZDO DESTROYED " + __instance.m_namedPrefabs[zdo.m_prefab]);
		}

		[HarmonyPrefix] [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Destroy))]
		private static bool Destroy_Prefix(ZNetScene __instance, GameObject go)
		{
			var component = go.GetComponent<ZNetView>();
			if ((bool)component && component.GetZDO() != null)
			{
				var zDO = component.GetZDO();
				component.ResetZDO();
				__instance.m_instances.Remove(zDO);
				if (!CreateRemoveHack)
				{
					ExternallySpawnedObjects.RemoveSwapBack(component);
				}
				if (zDO.IsOwner())
				{
					ZDOMan.instance.DestroyZDO(zDO);
				}
			}
			Object.Destroy(go);

			return false;
		}

		[HarmonyPostfix] [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.AddToSector))]
		public static void ZDOMan_AddToSector_Postfix(ZDOMan __instance, ZDO zdo, Vector2i sector)
		{
			if (CreateRemoveHack || zdo.GetPrefab() == 0 || zdo.IsOwner()) { return; }

			VPO.Logger.LogInfo($"WTF ADDED {ZNetScene.instance.GetPrefab(zdo.GetPrefab())}");

			if (zdo.m_distant)
			{
				QueuedDistantObjects.Add(zdo);
			}
			else
			{
				QueuedNearObjects.Add(zdo);
			}
		}

		[HarmonyPostfix] [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.RemoveFromSector))]
		public static void ZDOMan_RemoveFromSector_Postfix(ZDOMan __instance, ZDO zdo, Vector2i sector)
		{
			if (CreateRemoveHack || zdo.GetPrefab() == 0 || zdo.IsOwner()) { return; }

			VPO.Logger.LogInfo($"WTF REMOVED {ZNetScene.instance.GetPrefab(zdo.GetPrefab())}");

			if (zdo.m_distant)
			{
				QueuedDistantObjects.RemoveSwapBack(zdo);
			}
			else
			{
				QueuedNearObjects.RemoveSwapBack(zdo);
			}
		}

		[HarmonyPrefix, HarmonyPatch(typeof(ZDO), nameof(ZDO.SetSector))]
		private static bool ZDO_SetSector_Prefix(ZDO __instance, Vector2i sector)
		{
			CreateRemoveHack = true;
			return true;
		}

		[HarmonyPostfix, HarmonyPatch(typeof(ZDO), nameof(ZDO.SetSector))]
		private static void ZDO_SetSector_Postfix(ZDO __instance, Vector2i sector)
		{
			CreateRemoveHack = false;
		}
		
		[HarmonyPrefix, HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Shutdown))]
		private static bool ZNetScene_Shutdown_Prefix(ZNetScene __instance)
		{
			QueuedNearObjects.Clear();
			QueuedDistantObjects.Clear();
			
			_lastNearZoneSet.Clear();
			_lastDistantZoneSet.Clear();
			
			RemoveQueue.Clear();
			ExternallySpawnedObjects.Clear();
			
			_currentZone = new Vector2i(Int32.MinValue, Int32.MinValue);
			
			return true;
		}

		private static int ReverseZDOCompare(ZDO x, ZDO y)
		{
			if (x.m_type == y.m_type)
			{
				return y.m_tempSortValue.CompareTo(x.m_tempSortValue);
			}

			if (x.m_type < y.m_type)
			{
				return -1;
			}
			return 1;
		}
	}
}
