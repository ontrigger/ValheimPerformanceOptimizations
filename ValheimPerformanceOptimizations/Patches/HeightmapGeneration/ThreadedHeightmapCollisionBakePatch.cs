using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using VPOBurst;

namespace ValheimPerformanceOptimizations.Patches.HeightmapGeneration;

/// <summary>
/// Heightmap generation is done entirely on one thread - this includes generating vertices,
/// baking the collision, creating and setting vertex colors.
/// This patch introduces several optimizations to the generation process,
/// such as baking the physics data over the course of one frame
/// or multithreading to generate vertex colors
/// </summary>
public static class ThreadedHeightmapCollisionBakePatch
{
	public static readonly Dictionary<Heightmap, bool> HeightmapFinished = new();
	private static readonly Dictionary<Vector2i, GameObject> SpawnedZones = new();

	private static ConfigEntry<bool> _threadedCollisionBakeEnabled;

	static ThreadedHeightmapCollisionBakePatch()
	{
		ValheimPerformanceOptimizations.OnInitialized += Initialize;
	}

	public static void Initialize(ConfigFile configFile, Harmony harmony)
	{
		const string key = "Threaded terrain collision baking enabled";
		const string description =
			"Experimental: if enabled terrain is generated in parallel, this reduces lag spikes when moving through the world. This is an experimental feature, please report any issues that may occur.";
		_threadedCollisionBakeEnabled = configFile.Bind("General", key, true, description);

		if (_threadedCollisionBakeEnabled.Value)
		{
			harmony.PatchAll(typeof(ThreadedHeightmapCollisionBakePatch));
		}
	}

	[HarmonyPatch(typeof(Heightmap), nameof(Heightmap.Awake))]
	[HarmonyPostfix]
	private static void AwakePatch(Heightmap __instance)
	{
		if (__instance.m_collider)
		{
			// cookingOptions has to be default, otherwise no pre-baking is possible
			__instance.m_collider.cookingOptions = MeshColliderCookingOptions.CookForFasterSimulation |
				MeshColliderCookingOptions.EnableMeshCleaning |
				MeshColliderCookingOptions.UseFastMidphase |
				MeshColliderCookingOptions.WeldColocatedVertices;
		}

		HeightmapFinished[__instance] = false;
	}

	private static void OnBakeDone(Heightmap heightmap)
	{
		if (heightmap == null) { return; }

		heightmap.m_collider.sharedMesh = heightmap.m_collisionMesh;
		HeightmapFinished[heightmap] = true;
	}

	// enqueue current collision mesh to be baked in the separate thread
	[HarmonyPatch(typeof(Heightmap), nameof(Heightmap.RebuildCollisionMesh))]
	[HarmonyPrefix]
	private static bool RebuildCollisionMeshPatch(Heightmap __instance)
	{
		var mesh = __instance.m_collisionMesh;
		if (mesh == null)
		{
			mesh = new Mesh();
		}

		var width = __instance.m_width;
		var scale = __instance.m_scale;
		var num = width + 1;
		var vertCount = num * num;
		var quadCount = width * width;
		var indexCount = quadCount * 6;

		var heights = new NativeArray<float>(vertCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
		var verts = new NativeArray<Vector3>(vertCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
		var minMax = new NativeArray<float>(2, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
		var indices = default(NativeArray<int>);
		
		NativeArray<float>.Copy(NoAllocHelpers.ExtractArrayFromList(__instance.m_heights), 0, heights, 0, vertCount);

		var needIndices = mesh.GetIndexCount(0) != indexCount;
		if (needIndices)
		{
			indices = new NativeArray<int>(indexCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
		}

		var vertsHandle = new GenerateCollisionVerticesJob
		{
			Width = width,
			Scale = scale,
			Heights = heights,
			Verts = verts,
			MinMax = minMax,
		}.Schedule();

		var indicesHandle = needIndices
			? new GenerateCollisionIndicesJob { Width = width, Indices = indices }.Schedule(quadCount, width)
			: default;

		JobHandle.CombineDependencies(vertsHandle, indicesHandle).Complete();

		var minHeight = minMax[0];
		var maxHeight = minMax[1];

		mesh.SetVertices(verts);
		if (needIndices)
		{
			mesh.SetIndices(indices, MeshTopology.Triangles, 0);
		}

		var halfExtent = width * scale * 0.5f;
		__instance.m_bounds.SetMinMax(
			__instance.transform.position + new Vector3(0f - halfExtent, minHeight, 0f - halfExtent),
			__instance.transform.position + new Vector3(halfExtent, maxHeight, halfExtent));
		__instance.m_boundingSphere.position = __instance.m_bounds.center;
		__instance.m_boundingSphere.radius
			= Vector3.Distance(__instance.m_boundingSphere.position, __instance.m_bounds.max);

		__instance.m_collisionMesh = mesh;

		// TODO: merge it all
		var deferBake = VPOTerrainCollisionBaker.Instance.RequestAsyncCollisionBake(__instance, OnBakeDone);
		if (__instance.m_collider && !deferBake)
		{
			__instance.m_collider.sharedMesh = mesh;
			HeightmapFinished[__instance] = true;
		}

		heights.Dispose();
		verts.Dispose();
		minMax.Dispose();
		if (indices.IsCreated)
		{
			indices.Dispose();
		}

		return false;
	}

	[HarmonyPatch(typeof(Heightmap), nameof(Heightmap.OnDestroy))]
	[HarmonyPostfix]
	private static void OnDestroyPatch(Heightmap __instance)
	{
		HeightmapFinished.Remove(__instance);

		if (!ZoneSystem.instance)
		{
			return;
		}

		var zonePos = ZoneSystem.GetZone(__instance.transform.position);
		SpawnedZones.Remove(zonePos);
	}

	// spawn the heightmap GameObject but not call any placement until the heightmap has a collision mesh
	[HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.SpawnZone))]
	[HarmonyPrefix]
	private static bool SpawnZone(
		ZoneSystem __instance, ref bool __result, Vector2i zoneID, ZoneSystem.SpawnMode mode, out GameObject root)
	{
		var zonePos = ZoneSystem.GetZonePos(zoneID);

		var componentInChildren = __instance.m_zonePrefab.GetComponentInChildren<Heightmap>();
		if (!HeightmapBuilder.instance.IsTerrainReady(zonePos, componentInChildren.m_width,
			    componentInChildren.m_scale,
			    componentInChildren.m_isDistantLod,
			    WorldGenerator.instance) || __instance.m_locationInstances.TryGetValue(zoneID, out var location) &&
		    !location.m_placed &&
		    !__instance.PokeCanSpawnLocation(location.m_location, true))
		{
			root = null;
			__result = false;
			return false;
		}

		root = GetOrCreateZone(__instance.m_zonePrefab, zoneID, zonePos);

		var heightmap = root.GetComponentInChildren<Heightmap>();
		if (!HeightmapFinished[heightmap])
		{
			__result = false;
			return false;
		}

		if ((mode == ZoneSystem.SpawnMode.Ghost || mode == ZoneSystem.SpawnMode.Full) &&
		    !__instance.IsZoneGenerated(zoneID))
		{
			__instance.m_tempClearAreas.Clear();
			__instance.m_tempSpawnedObjects.Clear();
			__instance.PlaceLocations(zoneID, zonePos, root.transform, heightmap, __instance.m_tempClearAreas, mode,
				__instance.m_tempSpawnedObjects);
			__instance.PlaceVegetation(zoneID, zonePos, root.transform, heightmap, __instance.m_tempClearAreas,
				mode, __instance.m_tempSpawnedObjects);
			__instance.PlaceZoneCtrl(zoneID, zonePos, mode, __instance.m_tempSpawnedObjects);
			if (mode == ZoneSystem.SpawnMode.Ghost)
			{
				foreach (var tempSpawnedObject in __instance.m_tempSpawnedObjects)
				{
					Object.Destroy(tempSpawnedObject);
				}

				__instance.m_tempSpawnedObjects.Clear();
				Object.Destroy(root);
				root = null;
			}

			__instance.SetZoneGenerated(zoneID);
		}

		__result = true;
		return false;
	}

	[HarmonyPatch(typeof(ClutterSystem), nameof(ClutterSystem.IsHeightmapReady))]
	[HarmonyPostfix]
	private static void IsHeightmapReadyPatch(ClutterSystem __instance, ref bool __result)
	{
		// only change the result if it was true
		if (!__result)
		{
			return;
		}

		var mainCamera = Utils.GetMainCamera();
		__result = IsHeightmapReady(mainCamera.transform.position);
	}

	private static bool IsHeightmapReady(Vector3 pos)
	{
		var any = false;
		var ready = true;

		foreach (var heightmap in Heightmap.s_heightmaps)
		{
			if (heightmap.IsPointInside(pos))
			{
				any = true;
				ready = ready && HeightmapFinished.ContainsKey(heightmap) && HeightmapFinished[heightmap];
			}
		}

		return any && ready;
	}

	[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Shutdown))]
	[HarmonyPostfix]
	public static void ZNetScene_Shutdown_Postfix(ZNetScene __instance)
	{
		SpawnedZones.Clear();
		HeightmapFinished.Clear();
	}

	private static GameObject GetOrCreateZone(GameObject zonePrefab, Vector2i zoneID, Vector3 zonePos)
	{
		GameObject zone;
		if (!SpawnedZones.ContainsKey(zoneID))
		{
			zone = Object.Instantiate(zonePrefab, zonePos, Quaternion.identity);
			SpawnedZones.Add(zoneID, zone);
		}
		else
		{
			zone = SpawnedZones[zoneID];
		}

		return zone;
	}
}
