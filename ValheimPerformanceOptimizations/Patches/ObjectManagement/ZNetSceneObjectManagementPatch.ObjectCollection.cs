using System.Collections.Generic;

namespace ValheimPerformanceOptimizations.Patches
{
	public static partial class ZNetSceneObjectManagementPatch
	{
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

		private static void CollectDistantZoneObjects(Vector2i sector, ICollection<ZDO> objects)
		{
			var instance = ZDOMan.instance;
			var num = instance.SectorToIndex(sector);

			if (num >= 0)
			{
				List<ZDO> sectorObjects = instance.m_objectsBySector[num];
				if (sectorObjects == null) { return; }

				for (var i = 0; i < sectorObjects.Count; i++)
				{
					if (sectorObjects[i].m_distant)
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
					if (sectorObjects[j].m_distant)
					{
						objects.Add(sectorObjects[j]);
					}
				}
			}

		}
		
		private static void CollectNearZoneObjects(Vector2i sector, ICollection<ZDO> objects)
		{
			var instance = ZDOMan.instance;
			var num = instance.SectorToIndex(sector);

			if (num >= 0)
			{
				List<ZDO> sectorObjects = instance.m_objectsBySector[num];
				if (sectorObjects == null) { return; }

				for (var i = 0; i < sectorObjects.Count; i++)
				{
					if (!sectorObjects[i].m_distant)
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
					if (!sectorObjects[j].m_distant)
					{
						objects.Add(sectorObjects[j]);
					}
				}
			}

		}
	}
}
