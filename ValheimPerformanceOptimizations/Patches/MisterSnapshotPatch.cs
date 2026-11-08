using System;
using System.Collections.Generic;
using HarmonyLib;
using Unity.Mathematics;
using UnityEngine;
using ValheimPerformanceOptimizations.Burst;

namespace ValheimPerformanceOptimizations.Patches;

// burst compiles distance checking, 
// removes native transform get overhead, 
// merges loops together
// 6ms -> 1.5ms
[HarmonyPatch]
internal static unsafe class MisterSnapshotPatch
{
	private static MisterSnapshot[] _snapshots = new MisterSnapshot[64];
	private static readonly Dictionary<Mister, int> SnapshotIndexByMister = new();
	private static int _snapshotCount;

	private static int[] _neighbors = new int[64];
	private static int _neighborCount;
	private static Mister _currentMister;

	private static DemisterSnapshot[] _demisterSnapshots = new DemisterSnapshot[64];
	private static readonly Dictionary<Demister, int> SnapshotIndexByDemister = new();
	private static int _demisterSnapshotCount;
	private static bool _calledFromUpdate;

	private static readonly Comparison<Mister> CompareByTempDistance =
		(a, b) => a.m_tempDistance.CompareTo(b.m_tempDistance);

	[HarmonyPrefix]
	[HarmonyPatch(typeof(Mister), nameof(Mister.GetDemistersSorted))]
	private static bool GetDemistersSorted_Prefix(Vector3 refPoint, ref List<Mister> __result)
	{
		var count = Mister.m_instances.Count;
		if (_snapshots.Length < count)
		{
			_snapshots = new MisterSnapshot[Mathf.NextPowerOfTwo(count)];
			_neighbors = new int[_snapshots.Length];
		}

		for (var i = 0; i < count; i += 1)
		{
			var mister = Mister.m_instances[i];
			var position = mister.transform.position;
			mister.m_tempDistance = Vector3.Distance(position, refPoint);
			SnapshotIndexByMister[mister] = i;
			_snapshots[i] = new MisterSnapshot
			{
				Position = position,
				Radius = mister.m_radius,
				Top = position.y + mister.m_height,
			};
		}

		Mister.m_instances.Sort(CompareByTempDistance);

		_snapshotCount = count;
		_calledFromUpdate = true;

		__result = Mister.m_instances;
		return false;
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(ParticleMist), nameof(ParticleMist.Update))]
	private static void ParticleMist_Update_Postfix()
	{
		_calledFromUpdate = false;
		SnapshotIndexByMister.Clear();
		_snapshotCount = 0;
		_currentMister = null;
		SnapshotIndexByDemister.Clear();
		_demisterSnapshotCount = 0;
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(ParticleMist), nameof(ParticleMist.GetAllForcefields))]
	private static void GetAllForcefields_Postfix(List<Demister> fields)
	{
		var count = fields.Count;
		if (_demisterSnapshots.Length < count)
		{
			_demisterSnapshots = new DemisterSnapshot[Mathf.NextPowerOfTwo(count)];
		}

		for (var i = 0; i < count; i += 1)
		{
			var demister = fields[i];
			SnapshotIndexByDemister[demister] = i;
			_demisterSnapshots[i] = new DemisterSnapshot
			{
				Position = demister.transform.position,
				EndRange = demister.m_forceField.endRange,
			};
		}

		_demisterSnapshotCount = count;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(ParticleMist), nameof(ParticleMist.IsInsideOtherDemister))]
	private static bool IsInsideOtherDemister_Prefix(
		ParticleMist __instance, List<Demister> fields, Vector3 p, float radius, Demister ignore, ref bool __result)
	{
		if (!_calledFromUpdate || fields != __instance.fields) { return true; }

		var ignoreIndex = ignore != null && SnapshotIndexByDemister.TryGetValue(ignore, out var index) ? index : -1;
		float3 point = p;

		fixed (DemisterSnapshot* snapshots = _demisterSnapshots)
		{
			__result = MisterChecks.IsInsideOtherDemister(snapshots, _demisterSnapshotCount, ignoreIndex, point, radius);
		}

		return false;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(Mister), nameof(Mister.IsCompletelyInsideOtherMister))]
	private static bool IsCompletelyInsideOtherMister_Prefix(Mister __instance, float thickness, ref bool __result)
	{
		if (!_calledFromUpdate) { return true; }

		_currentMister = null;

		if (!SnapshotIndexByMister.TryGetValue(__instance, out var myIndex)) { return true; }

		int neighborCount;
		fixed (MisterSnapshot* snapshots = _snapshots)
		fixed (int* neighbors = _neighbors)
		{
			__result = MisterChecks.IsCompletelyInsideOther(
				snapshots, _snapshotCount, myIndex, thickness, neighbors, &neighborCount);
		}

		if (!__result)
		{
			_currentMister = __instance;
			_neighborCount = neighborCount;
		}

		return false;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(Mister), nameof(Mister.InsideMister))]
	private static bool InsideMister_Prefix(Vector3 p, float radius, ref bool __result)
	{
		if (!_calledFromUpdate) { return true; }

		float3 point = p;

		fixed (MisterSnapshot* snapshots = _snapshots)
		{
			__result = MisterChecks.InsideAny(snapshots, _snapshotCount, point, radius);
		}

		return false;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(Mister), nameof(Mister.IsInsideOtherMister))]
	private static bool IsInsideOtherMister_Prefix(Vector3 p, Mister ignore, ref bool __result)
	{
		if (!_calledFromUpdate || _currentMister == null || ignore != _currentMister) { return true; }

		float3 point = p;

		fixed (MisterSnapshot* snapshots = _snapshots)
		fixed (int* neighbors = _neighbors)
		{
			__result = MisterChecks.IsInsideAny(snapshots, neighbors, _neighborCount, point);
		}

		return false;
	}
}
