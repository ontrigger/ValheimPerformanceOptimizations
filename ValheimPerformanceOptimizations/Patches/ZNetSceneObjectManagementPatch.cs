using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Profiling;
namespace ValheimPerformanceOptimizations.Patches
{
	using VPO = ValheimPerformanceOptimizations;
	public static class ZNetSceneObjectManagementPatch
	{
		public static bool CreateRemoveHack;

		private static Vector2i currentZone = new Vector2i(Int32.MinValue, Int32.MinValue);

		private static HashSet<Vector2i> lastNearZoneSet = new HashSet<Vector2i>();
		private static HashSet<Vector2i> lastDistantZoneSet = new HashSet<Vector2i>();

		private static HashSet<Vector2i> nearZonesToUnload;
		private static HashSet<Vector2i> nearZonesToLoad;

		private static HashSet<Vector2i> distantZonesToUnload;
		private static HashSet<Vector2i> distantZonesToLoad;

		private static readonly Dictionary<Vector2i, List<ZDO>> QueuedNearObjects = new Dictionary<Vector2i, List<ZDO>>();
		private static readonly Dictionary<Vector2i, List<ZDO>> QueuedDistantObjects = new Dictionary<Vector2i, List<ZDO>>();

		private static readonly HashSet<ZDO> RemoveQueue = new HashSet<ZDO>();

		[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.CreateDestroyObjects)), HarmonyPrefix]
		public static bool ZNetScene_CreateDestroyObjects_Prefix(ZNetScene __instance)
		{
			var refZone = ZoneSystem.instance.GetZone(ZNet.instance.GetReferencePosition());
			__instance.m_tempCurrentObjects.Clear();
			__instance.m_tempCurrentDistantObjects.Clear();

			var what = QueuedNearObjects
				.Where(pair => pair.Value.Count == 0)
				.Select(pair => pair.Key)
				.ToList();
			foreach (var key in what)
			{
				QueuedNearObjects.Remove(key);
			}

			var what2 = QueuedDistantObjects
				.Where(pair => pair.Value.Count == 0)
				.Select(pair => pair.Key)
				.ToList();
			foreach (var key in what2)
			{
				QueuedDistantObjects.Remove(key);
			}

			Profiler.BeginSample("my cool logic");
			if (refZone != currentZone)
			{
				var (nearZones, distantZones) = GetActiveZoneSet(refZone);
				VPO.Logger.LogInfo($"Changed zone from {currentZone} to {refZone}");

				Profiler.BeginSample("near");
				nearZonesToUnload = new HashSet<Vector2i>(lastNearZoneSet.Except(nearZones));
				nearZonesToLoad = new HashSet<Vector2i>(nearZones.Except(lastNearZoneSet));

				distantZonesToUnload = new HashSet<Vector2i>(lastDistantZoneSet.Except(distantZones));
				distantZonesToLoad = new HashSet<Vector2i>(distantZones.Except(lastDistantZoneSet));

				foreach (var zone in nearZonesToLoad)
				{
					var toCreate = new List<ZDO>();
					if (lastDistantZoneSet.Contains(zone))
					{
						// we moved into a zone that was previously distant, collect only non distant zdos
						// the zone might not have fully loaded, so watch out for queuedDistantObjects
						CollectZoneObjects(zone, toCreate, zdo => !zdo.m_distant);
						QueuedNearObjects[zone] = toCreate;
					}
					else
					{
						// we teleported in the zone, hence it has no previous distant objects
						CollectAllZoneObjects(zone, toCreate);
						QueuedNearObjects[zone] = toCreate;
					}
				}
				
				VPO.Logger.LogInfo($"nearZonesToLoad {String.Join("|", nearZonesToLoad)}");

				Profiler.EndSample();

				Profiler.BeginSample("far");

				foreach (var zone in nearZonesToUnload)
				{
					if (distantZonesToUnload.Contains(zone))
					{
						// we moved forward so that a near zone became a distant zone,
						// remove all non-distant objects from it
						var moveables = QueuedNearObjects[zone].Where(zdo => !zdo.m_distant);
						QueuedNearObjects[zone].RemoveAll(zdo => !zdo.m_distant);
						foreach (var moveable in moveables)
						{
							//RemoveQueue.Add(moveable);
						}
					}
					else
					{
						// we teleported to the zone, remove all queued stuff
						QueuedNearObjects.Remove(zone);
					}
				}
				
				foreach (var zone in distantZonesToUnload)
				{
					if (QueuedDistantObjects.TryGetValue(zone, out var zdoList))
					{
						foreach (var zdo in zdoList)
						{
							RemoveQueue.Add(zdo);
						}
					}
						
					QueuedDistantObjects.Remove(zone);
				}

				var toAdd = new List<Vector2i>();
				foreach (var zone in distantZonesToLoad)
				{
					var toCreate = new List<ZDO>();
					// if we moved forward and a near zone became a distant zone, 
					// the loop above will remove all non distant objects from the creation queue
					// this is done to match vanilla behavior

					//if (!lastNearZoneSet.Contains(zone))
					//{
					CollectZoneObjects(zone, toCreate, (zdo) => 
						zdo.m_distant && (!lastNearZoneSet.Contains(zdo.m_sector) || zdo.m_tempCreateEarmark == -1));

					QueuedDistantObjects[zone] = toCreate;
					VPO.Logger.LogInfo(String.Join("|", toCreate.GroupBy(zdo => zdo.m_dataRevision).Select(group => $"{group.Key}")));
					toAdd.Add(zone);
					//}
				}

				VPO.Logger.LogInfo($"Addin distant {String.Join("|", toAdd)}");
				
				VPO.Logger.LogInfo($"Unloadin zones {String.Join("|", distantZonesToUnload)}");  
				
				Profiler.EndSample();

				lastNearZoneSet = nearZones;
				lastDistantZoneSet = distantZones;
			}

			Profiler.EndSample();
			currentZone = refZone;

			Profiler.BeginSample("vanilla");
			ZDOMan.instance.FindSectorObjects(refZone, ZoneSystem.instance.m_activeArea, ZoneSystem.instance.m_activeDistantArea, __instance.m_tempCurrentObjects, __instance.m_tempCurrentDistantObjects);
			CreateRemoveHack = true;
			__instance.CreateObjects(__instance.m_tempCurrentObjects, __instance.m_tempCurrentDistantObjects);
			__instance.RemoveObjects(__instance.m_tempCurrentObjects, __instance.m_tempCurrentDistantObjects);
			CreateRemoveHack = false;
			Profiler.EndSample();

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
		private static bool CreateObjectsSorted(ZNetScene __instance, List<ZDO> currentNearObjects, int maxCreatedPerFrame, ref int created)
		{
			if (!ZoneSystem.instance.IsActiveAreaLoaded())
			{
				return false;
			}

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

			/*var correctObjects = new HashSet<ZDO>();
			__instance.m_tempCurrentObjects2.ForEach(zdo => correctObjects.Add(zdo));

			var objectsMine = new HashSet<ZDO>(QueuedNearObjects.Values.SelectMany(zdos => zdos));

			var notInZonesToLoad = correctObjects.Except(objectsMine).ToList();
			var notInCorrectSectors = objectsMine.Except(correctObjects).ToList();
			if (notInCorrectSectors.Count > 0 || notInZonesToLoad.Count > 0)
			{
				VPO.Logger.LogInfo($"NEAR? {notInCorrectSectors.Count} {notInZonesToLoad.Count}");
			}
			
			if (notInZonesToLoad.Count > 0)
			{
				VPO.Logger.LogInfo($"NEAR3 {String.Join("|", new HashSet<Vector2i>(notInZonesToLoad.Select(zdo => zdo.m_sector)))}");
			}*/

			foreach (ZDO item in __instance.m_tempCurrentObjects2)
			{
				if (__instance.CreateObject(item) != null)
				{
					QueuedNearObjects[item.m_sector].RemoveSwapBack(item);
					created++;
					if (created > num)
					{
						break;
					}
				}
				else if (ZNet.instance.IsServer())
				{
					item.SetOwner(ZDOMan.instance.GetMyID());
					ZDOID uid = item.m_uid;
					ZLog.Log("Destroyed invalid predab ZDO:" + uid.ToString());
					ZDOMan.instance.DestroyZDO(item);
				}
			}

			return false;
		}

		[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.CreateDistantObjects)), HarmonyPrefix]
		private static bool CreateDistantObjects(ZNetScene __instance, List<ZDO> objects, int maxCreatedPerFrame, ref int created)
		{
			if (created > maxCreatedPerFrame)
			{
				return false;
			}

			int frameCount = Time.frameCount;
			var correctObjects = new HashSet<ZDO>();
			objects
				.Where(zdo => zdo.m_tempCreateEarmark != frameCount)
				.ToList()
				.ForEach(zdo => correctObjects.Add(zdo));

			var objectsMine = new HashSet<ZDO>(QueuedDistantObjects.Values.SelectMany(zdos => zdos));

			var notInZonesToLoad = correctObjects.Except(objectsMine).ToList();
			var notInCorrectSectors = objectsMine.Except(correctObjects).ToList();
			if (notInCorrectSectors.Count > 0 || notInZonesToLoad.Count > 0)
			{
				VPO.Logger.LogInfo($"DISTANT? {notInCorrectSectors.Count} {notInZonesToLoad.Count}");
			}

			if (notInZonesToLoad.Count > 0)
			{
				VPO.Logger.LogInfo($"DISTANT2 {String.Join("|", new HashSet<Vector2i>(notInZonesToLoad.Select(zdo => zdo.m_sector)))}");
				VPO.Logger.LogInfo($"DISTANT2 {String.Join("|", notInZonesToLoad.Select(zdo => $"{zdo.m_dataRevision} {zdo.m_uid}"))}");
				VPO.Logger.LogInfo($"ANY IN REMOVE Q? {notInZonesToLoad.Count(zdo => RemoveQueue.Contains(zdo))}");
			}

			if (notInCorrectSectors.Count > 0)
			{
				VPO.Logger.LogInfo($"DISTANT3  {String.Join("|", new HashSet<Vector2i>(notInCorrectSectors.Select(zdo => zdo.m_sector)))}");
			}

			foreach (ZDO @object in objects)
			{
				if (@object.m_tempCreateEarmark == frameCount)
				{
					continue;
				}
				if (__instance.CreateObject(@object) != null)
				{
					if (!QueuedDistantObjects.ContainsKey(@object.m_sector))
					{
						VPO.Logger.LogInfo($"WHY {@object.m_sector}");
					}
					QueuedDistantObjects[@object.m_sector].RemoveSwapBack(@object);
					created++;
					if (created > maxCreatedPerFrame)
					{
						break;
					}
				}
				else if (ZNet.instance.IsServer())
				{
					@object.SetOwner(ZDOMan.instance.GetMyID());
					ZDOID uid = @object.m_uid;
					ZLog.Log("Destroyed invalid predab ZDO:" + uid.ToString() + "  prefab hash:" + @object.GetPrefab());
					ZDOMan.instance.DestroyZDO(@object);
				}
			}

			return false;
		}

		[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.RemoveObjects)), HarmonyPrefix]
		private static bool RemoveObjects(ZNetScene __instance, List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)
		{
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
				RemoveQueue.Remove(zDO);
			}

			/*for (var i = RemoveQueue.Count - 1; i >= 0; i--)
			{
				var zdo = RemoveQueue[i];
				var zNetView = __instance.m_instances[zdo];
				zNetView.ResetZDO();
				UnityEngine.Object.Destroy(zNetView.gameObject);
				if (!zdo.m_persistent && zdo.IsOwner())
				{
					ZDOMan.instance.DestroyZDO(zdo);
				}
				__instance.m_instances.Remove(zdo);
				RemoveQueue.RemoveAt(i);
			}*/

			return false;
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
			if (num >= 0)
			{
				if (__instance.m_objectsBySector[num] != null)
				{
					if (zdo.m_tempCreateEarmark >= 0)
					{
						VPO.Logger.LogInfo($"ADDD {ZNetScene.instance.GetPrefab(zdo.m_prefab)} {zdo.m_sector}");
					}
					__instance.m_objectsBySector[num].Add(zdo);
					return false;
				}
				List<ZDO> list = new List<ZDO>();
				list.Add(zdo);
				__instance.m_objectsBySector[num] = list;
				if (zdo.m_tempCreateEarmark >= 0)
				{
					VPO.Logger.LogInfo($"ADDD {ZNetScene.instance.GetPrefab(zdo.m_prefab)} {zdo.m_sector}");
				}
			}
			else if (__instance.m_objectsByOutsideSector.TryGetValue(sector, out value))
			{
				if (zdo.m_tempCreateEarmark >= 0)
				{
					VPO.Logger.LogInfo($"ADDD OUTSIDE {ZNetScene.instance.GetPrefab(zdo.m_prefab)} {zdo.m_sector}");
				}
				value.Add(zdo);
			}
			else
			{
				value = new List<ZDO>();
				value.Add(zdo);
				__instance.m_objectsByOutsideSector.Add(sector, value);
				if (zdo.m_tempCreateEarmark >= 0)
				{
					VPO.Logger.LogInfo($"ADDD OUTSIDE {ZNetScene.instance.GetPrefab(zdo.m_prefab)} {zdo.m_sector}");
				}
			}

			return false;
		}

		[HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.RemoveFromSector)), HarmonyPrefix]
		public static void RemoveFromSector(ZDOMan __instance, ZDO zdo, Vector2i sector)
		{
			int num = __instance.SectorToIndex(sector);
			List<ZDO> value;
			if (num >= 0)
			{
				if (__instance.m_objectsBySector[num] != null)
				{
					__instance.m_objectsBySector[num].Remove(zdo);
				}
			}
			else if (__instance.m_objectsByOutsideSector.TryGetValue(sector, out value))
			{
				value.Remove(zdo);
			}
		}

		[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.AddInstance)), HarmonyPrefix]
		public static bool AddInstancePrefix(ZNetScene __instance, ZDO zdo, ZNetView nview)
		{
			__instance.m_instances[zdo] = nview;

			return false;
		}

		[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.OnZDODestroyed)), HarmonyPrefix]
		private static bool OnZDODestroyed(ZNetScene __instance, ZDO zdo)
		{
			if (__instance.m_instances.TryGetValue(zdo, out var value))
			{
				if (!CreateRemoveHack)
				{
					VPO.Logger.LogInfo($"Removin destroy {value.name} {value.m_zdo.m_sector}");
				}
				value.ResetZDO();
				UnityEngine.Object.Destroy(value.gameObject);
				__instance.m_instances.Remove(zdo);
			}

			return false;
		}
	}
}
