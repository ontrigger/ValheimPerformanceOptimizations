using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;
namespace ValheimPerformanceOptimizations.Patches
{
	// [HarmonyPatch]
	public static class ZNetSceneObjectManagementPatch
	{
		private static Vector2i currentZone = new Vector2i(Int32.MinValue, Int32.MinValue);

		private static HashSet<Vector2i> currentNearZoneSet = new HashSet<Vector2i>();
		private static HashSet<Vector2i> currentDistantZoneSet = new HashSet<Vector2i>();

		private static readonly List<ZDO> nearObjectsToCreate = new List<ZDO>();
		private static readonly List<ZDO> nearObjectsToDestroy = new List<ZDO>();

		private static List<ZDO> distantObjectsToCreate = new List<ZDO>();
		private static List<ZDO> distantObjectsToDestroy = new List<ZDO>();

		private static HashSet<Vector2i> distantZonesInLoading = new HashSet<Vector2i>();

		private static HashSet<Vector2i> lastDistantZonesToLoad = new HashSet<Vector2i>();

		private static HashSet<Vector2i> zonesWithNewObjects = new HashSet<Vector2i>();

		[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.CreateDestroyObjects)), HarmonyPrefix]
		public static bool ZNetScene_CreateDestroyObjects_Prefix(ZNetScene __instance)
		{
			var refZone = ZoneSystem.instance.GetZone(ZNet.instance.GetReferencePosition());
			__instance.m_tempCurrentObjects.Clear();
			__instance.m_tempCurrentDistantObjects.Clear();

			Profiler.BeginSample("my cool logic");
			if (refZone != currentZone || zonesWithNewObjects.Count > 0)
			{
				var (nearZones, distantZones) = GetActiveZoneSet(refZone);

				/*
				 * near load: if cell is marked as "distant loading" then only get near objs, else get all
				 * near unload: get all near objs to unload, remove them from create queue and destroy
				 * far load: queue shit up for load, mark as "distant loading"
				 * far unload: get all distant objs to unload, remove them from create queue and destroy
				 */

				Profiler.BeginSample("near");
				var nearZonesToUnload = new HashSet<Vector2i>(currentNearZoneSet.Except(nearZones));
				var nearZonesToLoad = new HashSet<Vector2i>(nearZones.Except(currentNearZoneSet));

				foreach (var zone in nearZonesToUnload)
				{
					CollectZoneObjects(zone, nearObjectsToDestroy, zdo => !zdo.m_distant);
				}

				// nearObjectsToCreate.RemoveAll(zdo => !zdo.m_distant && nearZonesToUnload.Contains(zdo.m_sector));

				foreach (var zone in nearZonesToLoad)
				{
					if (distantZonesInLoading.Contains(zone))
					{
						CollectZoneObjects(zone, nearObjectsToCreate, zdo => !zdo.m_distant);
					}
					else
					{
						CollectAllZoneObjects(zone, nearObjectsToCreate);
					}
				}
				
				Profiler.EndSample();

				
				Profiler.BeginSample("far");
				var distantZonesToUnload = new HashSet<Vector2i>(currentDistantZoneSet.Except(distantZones));
				var distantZonesToLoad = new HashSet<Vector2i>(distantZones.Except(currentDistantZoneSet));

				ValheimPerformanceOptimizations.Logger.LogInfo("DISTANT ZONES TO LOAD " + distantZonesToLoad.Join());
				ValheimPerformanceOptimizations.Logger.LogInfo("DISTANT ZONES TO DESTROY " + distantZonesToUnload.Join());

				foreach (var zone in distantZonesToUnload)
				{
					CollectZoneObjects(zone, distantObjectsToDestroy, zdo => zdo.m_distant);
				}

				distantObjectsToCreate.RemoveAll(zdo => distantZonesToUnload.Contains(zdo.m_sector));

				foreach (var zone in distantZonesToLoad)
				{
					distantZonesInLoading.Add(zone);
					CollectZoneObjects(zone, distantObjectsToCreate, zdo => zdo.m_distant);
				}
				
				Profiler.EndSample();

				currentNearZoneSet = nearZones;
				currentDistantZoneSet = distantZones;

				lastDistantZonesToLoad = distantZonesToLoad;
			}

			Profiler.EndSample();
			currentZone = refZone;

			Profiler.BeginSample("vanilla");
			ZDOMan.instance.FindSectorObjects(refZone, ZoneSystem.instance.m_activeArea, ZoneSystem.instance.m_activeDistantArea, __instance.m_tempCurrentObjects, __instance.m_tempCurrentDistantObjects);
			__instance.CreateObjects(__instance.m_tempCurrentObjects, __instance.m_tempCurrentDistantObjects);
			__instance.RemoveObjects(__instance.m_tempCurrentObjects, __instance.m_tempCurrentDistantObjects);
			Profiler.EndSample();
			
			zonesWithNewObjects.Clear();

			return false;
		}

		private static (HashSet<Vector2i>, HashSet<Vector2i>) GetActiveZoneSet(Vector2i zone)
		{
			var nearArea = ZoneSystem.instance.m_activeArea;
			var nearSectors = new HashSet<Vector2i> { zone };
			for (var i = 1; i <= nearArea; i++)
			{
				for (var j = zone.x - i; j <= zone.x + i; j++)
				{
					nearSectors.Add(new Vector2i(j, zone.y - i));
					nearSectors.Add(new Vector2i(j, zone.y + i));
				}
				for (var k = zone.y - i + 1; k <= zone.y + i - 1; k++)
				{
					nearSectors.Add(new Vector2i(zone.x - i, k));
					nearSectors.Add(new Vector2i(zone.x + i, k));
				}
			}

			var distantArea = ZoneSystem.instance.m_activeDistantArea;
			var distantSectors = new HashSet<Vector2i>();
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

			return (nearSectors, distantSectors);
		}

		private static void CollectZoneObjects(Vector2i sector, ICollection<ZDO> objects, Predicate<ZDO> predicate)
		{
			var instance = ZDOMan.instance;
			var num = instance.SectorToIndex(sector);

			if (num >= 0)
			{
				var sectorObjects = instance.m_objectsBySector[num];
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
				if (!instance.m_objectsByOutsideSector.TryGetValue(sector, out var sectorObjects))
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

		private static void CollectAllZoneObjects(Vector2i zone, List<ZDO> objects)
		{
			var instance = ZDOMan.instance;
			var zoneId = instance.SectorToIndex(zone);
			if (zoneId >= 0)
			{
				if (instance.m_objectsBySector[zoneId] != null)
				{
					objects.AddRange(instance.m_objectsBySector[zoneId]);
				}
			}
			else if (instance.m_objectsByOutsideSector.TryGetValue(zone, out var value))
			{
				objects.AddRange(value);
			}
		}

		[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.CreateObjectsSorted)), HarmonyPrefix]
		private static bool ZNetScene_CreateObjectsSorted_Prefix(ZNetScene __instance, List<ZDO> currentNearObjects, int maxCreatedPerFrame, ref int created)
		{
			if (!ZoneSystem.instance.IsActiveAreaLoaded())
			{
				return false;
			}
			Profiler.BeginSample("spawnin near");
			__instance.m_tempCurrentObjects2.Clear();
			int frameCount = Time.frameCount;
			Vector3 referencePosition = ZNet.instance.GetReferencePosition();
			foreach (ZDO currentNearObject in currentNearObjects)
			{
				if (currentNearObject.m_tempCreateEarmark != frameCount)
				{
					currentNearObject.m_tempSortValue = Utils.DistanceSqr(referencePosition, currentNearObject.GetPosition());
					__instance.m_tempCurrentObjects2.Add(currentNearObject);
				}
			}
			int num = Mathf.Max(__instance.m_tempCurrentObjects2.Count / 100, maxCreatedPerFrame);
			__instance.m_tempCurrentObjects2.Sort(ZNetScene.ZDOCompare);

			var refObjects = new HashSet<ZDO>(__instance.m_tempCurrentObjects2);
			refObjects.ExceptWith(nearObjectsToCreate);
			if (refObjects.Count > 0)
			{
				ValheimPerformanceOptimizations.Logger.LogInfo($"WRONG: {nearObjectsToCreate.Count} CORRECT: {__instance.m_tempCurrentObjects2.Count}");
			}

			foreach (ZDO item in __instance.m_tempCurrentObjects2)
			{
				if (__instance.CreateObject(item) != null)
				{
					created++;
					nearObjectsToCreate.Remove(item);
					if (created > num)
					{
						break;
					}
				}
				else if (ZNet.instance.IsServer())
				{
					item.SetOwner(ZDOMan.instance.GetMyID());
					ZLog.Log("Destroyed invalid predab ZDO:" + item.m_uid);
					ZDOMan.instance.DestroyZDO(item);
				}
			}

			Profiler.EndSample();

			return false;
		}

		[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.CreateDistantObjects)), HarmonyPrefix]
		private static bool ZNetScene_CreateDistantObjects_Prefix(ZNetScene __instance, List<ZDO> objects, int maxCreatedPerFrame, ref int created)
		{
			if (created > maxCreatedPerFrame)
			{
				return false;
			}
			Profiler.BeginSample("spawn distants");
			int frameCount = Time.frameCount;

			var correctObjCount = objects.Where(obj => obj.m_tempCreateEarmark != frameCount).ToList();
			var refObjects = new HashSet<ZDO>(correctObjCount);
			var test = refObjects.Except(distantObjectsToCreate).ToList();
			if (test.Count > 0)
			{
				var test2 = new HashSet<Vector2i>();
				ValheimPerformanceOptimizations.Logger.LogInfo($"DISTANT WRONG: {distantObjectsToCreate.Count} CORRECT: {correctObjCount.Count}");
				test.ForEach(zdo => test2.Add(zdo.m_sector));
				
				var test3 = test.Join(zdo =>
				{
					var prefabName = __instance.GetPrefab(zdo.m_prefab).name;
					var wasInZoneToLoad = lastDistantZonesToLoad.Contains(zdo.m_sector);
					return $"{prefabName}:{wasInZoneToLoad}";
				});
				
				ValheimPerformanceOptimizations.Logger.LogInfo("MISSING ZONES " + test2.Join());
				ValheimPerformanceOptimizations.Logger.LogInfo(test3);
			}
			
			foreach (ZDO @object in objects)
			{
				if (@object.m_tempCreateEarmark == frameCount)
				{
					continue;
				}
				if (__instance.CreateObject(@object) != null)
				{
					created++;
					distantObjectsToCreate.Remove(@object);
					if (created > maxCreatedPerFrame)
					{
						break;
					}
				}
				else if (ZNet.instance.IsServer())
				{
					@object.SetOwner(ZDOMan.instance.GetMyID());
					ZLog.Log(string.Concat("Destroyed invalid predab ZDO:", @object.m_uid, "  prefab hash:", @object.GetPrefab()));
					ZDOMan.instance.DestroyZDO(@object);
				}
			}
			Profiler.EndSample();

			Profiler.BeginSample("check zones in load");
			var zonesStillLoading = new HashSet<Vector2i>();
			foreach (var zdo in distantObjectsToCreate)
			{
				zonesStillLoading.Add(zdo.m_sector);
			}
			distantZonesInLoading = zonesStillLoading;
			Profiler.EndSample();

			return false;
		}

		[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.RemoveObjects)), HarmonyPrefix]
		private static void ZNetScene_RemoveObjects_Prefix(ZNetScene __instance, List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)
		{
			Profiler.BeginSample("deletin objs");
			int frameCount = Time.frameCount;
			foreach (ZDO currentNearObject in currentNearObjects)
			{
				currentNearObject.m_tempRemoveEarmark = frameCount;
			}
			foreach (ZDO currentDistantObject in currentDistantObjects)
			{
				currentDistantObject.m_tempRemoveEarmark = frameCount;
			}
			__instance.m_tempRemoved.Clear();
			foreach (ZNetView value in __instance.m_instances.Values)
			{
				if (value.GetZDO().m_tempRemoveEarmark != frameCount)
				{
					__instance.m_tempRemoved.Add(value);
				}
			}
			for (int i = 0; i < __instance.m_tempRemoved.Count; i++)
			{
				ZNetView zNetView = __instance.m_tempRemoved[i];
				ZDO zDO = zNetView.GetZDO();
				zNetView.ResetZDO();
				UnityEngine.Object.Destroy(zNetView.gameObject);
				if (!zDO.m_persistent && zDO.IsOwner())
				{
					ZDOMan.instance.DestroyZDO(zDO);
				}
				__instance.m_instances.Remove(zDO);
			}
			Profiler.EndSample();

			nearObjectsToDestroy.Clear();
			distantObjectsToDestroy.Clear();
		}
		
		[HarmonyPatch(typeof(ZDO), nameof(ZDO.SetSector)), HarmonyPrefix]
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
		}
		
		[HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.AddToSector)), HarmonyPrefix]
		public static bool AddToSector(ZDOMan __instance, ZDO zdo, Vector2i sector)
		{
			int num = __instance.SectorToIndex(sector);
			List<ZDO> value;
			ValheimPerformanceOptimizations.Logger.LogInfo("addin zdo " + zdo.m_sector);
			if (num >= 0)
			{
				if (__instance.m_objectsBySector[num] != null)
				{
					__instance.m_objectsBySector[num].Add(zdo);
					return false;
				}
				List<ZDO> list = new List<ZDO>();
				list.Add(zdo);
				__instance.m_objectsBySector[num] = list;
			}
			else if (__instance.m_objectsByOutsideSector.TryGetValue(sector, out value))
			{
				value.Add(zdo);
			}
			else
			{
				value = new List<ZDO>();
				value.Add(zdo);
				__instance.m_objectsByOutsideSector.Add(sector, value);
			}

			return false;
		}
	}
}
