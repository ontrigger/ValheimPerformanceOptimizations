using System.Collections.Generic;

namespace ValheimPerformanceOptimizations.Patches.ObjectManagement;

public static partial class ZNetSceneObjectManagementPatch
{
	private static readonly HashSet<Vector2s> ActiveNearZones = new();
	private static readonly HashSet<Vector2s> ActiveDistantZones = new();
	private static readonly HashSet<Vector2s> NextNearZones = new();
	private static readonly HashSet<Vector2s> NextDistantZones = new();
	private static readonly HashSet<Vector2s> ChangedZones = new();

	private static void BuildActiveZoneSets(Vector2s zone, SimulationDistance simulationDistance)
	{
		NextNearZones.Clear();
		NextDistantZones.Clear();
		NextNearZones.Add(zone);

		for (var distance = 1; distance <= simulationDistance.NearSimulationDistance; distance++)
		{
			for (var x = zone.x - distance; x <= zone.x + distance; x++)
			{
				AddNearSector(zone, new Vector2s((short)x, (short)(zone.y - distance)), simulationDistance);
				AddNearSector(zone, new Vector2s((short)x, (short)(zone.y + distance)), simulationDistance);
			}

			for (var y = zone.y - distance + 1; y <= zone.y + distance - 1; y++)
			{
				AddNearSector(zone, new Vector2s((short)(zone.x - distance), (short)y), simulationDistance);
				AddNearSector(zone, new Vector2s((short)(zone.x + distance), (short)y), simulationDistance);
			}
		}

		var firstDistantDistance = simulationDistance.IsClassic
			? simulationDistance.NearSimulationDistance + 1
			: 1;
		for (var distance = firstDistantDistance; distance <= simulationDistance.TotalSimulationDistance; distance++)
		{
			for (var x = zone.x - distance; x <= zone.x + distance; x++)
			{
				var bottom = new Vector2s((short)x, (short)(zone.y - distance));
				AddDistantSector(zone, bottom, simulationDistance);
				AddDistantSector(zone, new Vector2s((short)x, (short)(zone.y + distance)),
					simulationDistance, bottom);
			}

			for (var y = zone.y - distance + 1; y <= zone.y + distance - 1; y++)
			{
				AddDistantSector(zone, new Vector2s((short)(zone.x - distance), (short)y), simulationDistance);
				AddDistantSector(zone, new Vector2s((short)(zone.x + distance), (short)y), simulationDistance);
			}
		}
	}

	private static void AddNearSector(
		Vector2s center, Vector2s sector, SimulationDistance simulationDistance)
	{
		if (simulationDistance.IsClassic
		    || ZoneSystem.instance.ZonesWithinRadius(
			    center, sector, simulationDistance.NearSimulationDistance))
		{
			NextNearZones.Add(sector);
		}
	}

	private static void AddDistantSector(
		Vector2s center, Vector2s sector, SimulationDistance simulationDistance,
		Vector2s? radiusTestSector = null)
	{
		var testSector = radiusTestSector ?? sector;
		if ((simulationDistance.IsClassic
		    || ZoneSystem.instance.ZonesWithinRadius(
			    center, testSector, simulationDistance.TotalSimulationDistance, true))
		    && !NextNearZones.Contains(sector))
		{
			NextDistantZones.Add(sector);
		}
	}

	private static void CollectChangedZones(
		HashSet<Vector2s> activeSectors, HashSet<Vector2s> nextSectors)
	{
		foreach (var sector in activeSectors)
		{
			if (!nextSectors.Contains(sector))
			{
				ChangedZones.Add(sector);
			}
		}

		foreach (var sector in nextSectors)
		{
			if (!activeSectors.Contains(sector))
			{
				ChangedZones.Add(sector);
			}
		}
	}
}
