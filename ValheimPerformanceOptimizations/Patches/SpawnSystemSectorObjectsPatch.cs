using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;

namespace ValheimPerformanceOptimizations.Patches;

// dont rescan zone zdos for every spawnlist unless we spawned something
// cache zdo instance counts per prefab
// 4ms - 1ms
[HarmonyPatch]
internal static class SpawnSystemSectorObjectsPatch
{
	private static readonly List<ZDO> CachedNearZDOs = new();
	private static bool HasCachedNearZDOs;

	private static readonly Dictionary<int, int> CountByPrefabHash = new();
	private static readonly Dictionary<int, int> EventCreatureCountByPrefabHash = new();

	[HarmonyPrefix]
	[HarmonyPatch(typeof(SpawnSystem), nameof(SpawnSystem.UpdateSpawning))] 
	private static void SpawnSystem_UpdateSpawning_Prefix()
	{
		HasCachedNearZDOs = false;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(SpawnSystem), nameof(SpawnSystem.Spawn))] 
	private static void SpawnSystem_Spawn_Prefix()
	{
		HasCachedNearZDOs = false;
	}

	[HarmonyTranspiler]
	[HarmonyPatch(typeof(SpawnSystem), nameof(SpawnSystem.UpdateSpawnList))] 
	private static IEnumerable<CodeInstruction> SpawnSystem_UpdateSpawnList_Transpiler(
		IEnumerable<CodeInstruction> instructions)
	{
		var findSectorObjects = AccessTools.Method(typeof(ZDOMan), nameof(ZDOMan.FindSectorObjects));
		var findSectorObjectsCached = AccessTools.Method(typeof(SpawnSystemSectorObjectsPatch), nameof(FindSectorObjectsCached));
		var getNrOfZDOInstances = AccessTools.Method(typeof(SpawnSystem), nameof(SpawnSystem.GetNrOfZDOInstances));
		var getNrOfZDOInstancesCached = AccessTools.Method(typeof(SpawnSystemSectorObjectsPatch), nameof(GetNrOfZDOInstancesCached));

		foreach (var instruction in instructions)
		{
			if (instruction.Calls(findSectorObjects))
			{
				instruction.opcode = OpCodes.Call;
				instruction.operand = findSectorObjectsCached;
			}
			else if (instruction.Calls(getNrOfZDOInstances))
			{
				instruction.operand = getNrOfZDOInstancesCached;
			}

			yield return instruction;
		}
	}

	private static void FindSectorObjectsCached(
		ZDOMan zdoMan, Vector2s sector, SimulationDistance simulationDistance, List<ZDO> sectorObjects,
		List<ZDO> distantSectorObjects)
	{
		if (!HasCachedNearZDOs)
		{
			Profiler.BeginSample("FindSectorObjects");
			CachedNearZDOs.Clear();
			zdoMan.FindSectorObjects(sector, simulationDistance, CachedNearZDOs, distantSectorObjects);
			HasCachedNearZDOs = true;
			CountByPrefabHash.Clear();
			EventCreatureCountByPrefabHash.Clear();
			Profiler.EndSample();
		}

		Profiler.BeginSample("CopySectorObjects");
		sectorObjects.AddRange(CachedNearZDOs);
		Profiler.EndSample();
	}

	private static int GetNrOfZDOInstancesCached(GameObject prefab, List<ZDO> zdos, bool eventCreaturesOnly)
	{
		var countByPrefabHash = eventCreaturesOnly ? EventCreatureCountByPrefabHash : CountByPrefabHash;
		var prefabHash = prefab.name.GetStableHashCode();
		if (!countByPrefabHash.TryGetValue(prefabHash, out var count))
		{
			Profiler.BeginSample("GetNrOfZDOInstances");
			count = SpawnSystem.GetNrOfZDOInstances(prefab, zdos, eventCreaturesOnly);
			countByPrefabHash[prefabHash] = count;
			Profiler.EndSample();
		}

		return count;
	}
}
