using System.Collections.Generic;

namespace ValheimPerformanceOptimizations.Patches.ObjectManagement
{
	public static partial class ZNetSceneObjectManagementPatch
	{
		private static void GetActiveZoneSet(
			Vector2s zone, HashSet<Vector2s> nearSectors, HashSet<Vector2s> distantSectors)
		{
			var nearArea = ZoneSystem.instance.m_activeArea;

			nearSectors.Add(zone);
			distantSectors.Add(zone);
			for (var i = 1; i <= nearArea; i++)
			{
				for (var j = zone.x - i; j <= zone.x + i; j++)
				{
					nearSectors.Add(new Vector2s(j, zone.y - i));
					nearSectors.Add(new Vector2s(j, zone.y + i));

					distantSectors.Add(new Vector2s(j, zone.y - i));
					distantSectors.Add(new Vector2s(j, zone.y + i));
				}
				for (var k = zone.y - i + 1; k <= zone.y + i - 1; k++)
				{
					nearSectors.Add(new Vector2s(zone.x - i, k));
					nearSectors.Add(new Vector2s(zone.x + i, k));

					distantSectors.Add(new Vector2s(zone.x - i, k));
					distantSectors.Add(new Vector2s(zone.x + i, k));
				}
			}

			var distantArea = ZoneSystem.instance.m_activeDistantArea;
			for (var l = nearArea + 1; l <= nearArea + distantArea; l++)
			{
				for (var m = zone.x - l; m <= zone.x + l; m++)
				{
					distantSectors.Add(new Vector2s(m, zone.y - l));
					distantSectors.Add(new Vector2s(m, zone.y + l));
				}

				for (var n = zone.y - l + 1; n <= zone.y + l - 1; n++)
				{
					distantSectors.Add(new Vector2s(zone.x - l, n));
					distantSectors.Add(new Vector2s(zone.x + l, n));
				}
			}
		}

		private static List<ZDO> GetSectorObjects(Vector2s sectorShort)
		{
			var sector = new Vector2i(sectorShort.x, sectorShort.y);
			var instance = ZDOMan.instance;
			var sectorIndex = instance.SectorToIndex(sector);
			if (sectorIndex >= 0)
			{
				return instance.m_objectsBySector[sectorIndex];
			}

			instance.m_objectsByOutsideSector.TryGetValue(sector, out var sectorObjects);
			return sectorObjects;
		}

		private static void CollectZoneObjects(
			Vector2s sectorShort, List<ZDO> objects, Dictionary<ZDO, int> objectIndices, bool distant)
		{
			var sectorObjects = GetSectorObjects(sectorShort);
			if (sectorObjects == null) { return; }

			for (var i = 0; i < sectorObjects.Count; i++)
			{
				var zdo = sectorObjects[i];
				if (zdo.Distant == distant)
				{
					AddUnique(objects, objectIndices, zdo);
				}
			}
		}

		private static void UnloadZoneObjects(
			Vector2s sectorShort, List<ZDO> creationQueue, Dictionary<ZDO, int> creationIndices, bool distant)
		{
			var sectorObjects = GetSectorObjects(sectorShort);
			if (sectorObjects == null) { return; }

			for (var i = 0; i < sectorObjects.Count; i++)
			{
				var zdo = sectorObjects[i];
				RemoveQueuedObject(creationQueue, creationIndices, zdo);
				if (zdo.Distant == distant)
				{
					AddUnique(RemoveQueue, RemoveQueueIndices, zdo);
				}
			}
		}
	}
}
