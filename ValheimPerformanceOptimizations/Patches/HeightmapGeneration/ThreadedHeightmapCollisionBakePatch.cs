using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ValheimPerformanceOptimizations.Patches.HeightmapGeneration
{
	/// <summary>
	/// Heightmap generation is done entirely on one thread - this includes generating vertices,
	/// baking the collision, creating and setting vertex colors.
	/// This patch introduces several optimizations to the generation process,
	/// such as baking the physics data over the course of one frame
	/// or multithreading to generate vertex colors
	/// </summary>
	public static class ThreadedHeightmapCollisionBakePatch
	{
		public static readonly Dictionary<Heightmap, bool> HeightmapFinished = new Dictionary<Heightmap, bool>();
		private static readonly Dictionary<Vector2i, GameObject> SpawnedZones = new Dictionary<Vector2i, GameObject>();

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
		
		[HarmonyPatch(typeof(Heightmap), nameof(Heightmap.Awake)), HarmonyPostfix]
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

		[HarmonyPatch(typeof(Heightmap), nameof(Heightmap.OnEnable)), HarmonyPostfix]
		private static void OnEnablePatch(Heightmap __instance)
		{
			if (!__instance.m_isDistantLod || !Application.isPlaying || __instance.m_distantLodEditorHax)
			{
				var deferBake = VPOTerrainCollisionBaker.Instance.RequestAsyncCollisionBake(__instance, OnBakeDone);

				// insta finish if we can't bake
				if (!deferBake)
				{
					HeightmapFinished[__instance] = true;
				}
			}
		}
		private static void OnBakeDone(Heightmap heightmap)
		{
			if (heightmap == null) { return; }

			heightmap.m_collider.sharedMesh = heightmap.m_collisionMesh;
			heightmap.m_dirty = true;
			HeightmapFinished[heightmap] = true;
		}

		// enqueue current collision mesh to be baked in the separate thread
		[HarmonyPatch(typeof(Heightmap), nameof(Heightmap.RebuildCollisionMesh)), HarmonyPrefix]
		private static bool RebuildCollisionMeshPatch(Heightmap __instance)
		{
			var mesh = __instance.m_collisionMesh;
			if (mesh == null)
			{
				mesh = new Mesh();
			}

			var width = __instance.m_width;
			var num = width + 1;
			var maxHeight = -999999f;
			var minHeight = 999999f;
			Heightmap.m_tempVertises.Clear();

			for (var idx = 0; idx < num * num; idx++)
			{
				var i = (int)math.floor((float)idx / num);
				var j = idx % num;

				var vtx = __instance.CalcVertex(j, i);
				Heightmap.m_tempVertises.Add(vtx);
				if (vtx.y > maxHeight)
				{
					maxHeight = vtx.y;
				}
				if (vtx.y < minHeight)
				{
					minHeight = vtx.y;
				}
			}

			mesh.SetVertices(Heightmap.m_tempVertises);
			var prevIndexCount = (num - 1) * (num - 1) * 6;
			if (mesh.GetIndexCount(0) != prevIndexCount)
			{
				Heightmap.m_tempIndices.Clear();
				for (var k = 0; k < num - 1; k++)
				{
					for (var l = 0; l < num - 1; l++)
					{
						int item2 = k * num + l;
						int item3 = k * num + l + 1;
						int item4 = (k + 1) * num + l + 1;
						int item5 = (k + 1) * num + l;
						Heightmap.m_tempIndices.Add(item2);
						Heightmap.m_tempIndices.Add(item5);
						Heightmap.m_tempIndices.Add(item3);
						Heightmap.m_tempIndices.Add(item3);
						Heightmap.m_tempIndices.Add(item5);
						Heightmap.m_tempIndices.Add(item4);
					}
				}
				mesh.SetIndices(Heightmap.m_tempIndices, MeshTopology.Triangles, 0);
			}

			var deferBake = VPOTerrainCollisionBaker.Instance.RequestAsyncCollisionBake(__instance, OnBakeDone);
			if (__instance.m_collider && !deferBake)
			{
				__instance.m_collider.sharedMesh = mesh;
			}

			var num5 = width * __instance.m_scale * 0.5f;
			__instance.m_bounds.SetMinMax(__instance.transform.position + new Vector3(0f - num5, minHeight, 0f - num5), __instance.transform.position + new Vector3(num5, maxHeight, num5));
			__instance.m_boundingSphere.position = __instance.m_bounds.center;
			__instance.m_boundingSphere.radius = Vector3.Distance(__instance.m_boundingSphere.position, __instance.m_bounds.max);

			__instance.m_collisionMesh = mesh;

			return false;
		}

		[HarmonyPatch(typeof(Heightmap), nameof(Heightmap.OnDestroy)), HarmonyPostfix]
		private static void OnDestroyPatch(Heightmap __instance)
		{
			if (!ZoneSystem.instance) return;

			var zonePos = ZoneSystem.instance.GetZone(__instance.transform.position);
			if (SpawnedZones.ContainsKey(zonePos))
			{
				SpawnedZones.Remove(zonePos);
			}
		}

		// spawn the heightmap GameObject but not call any placement until the heightmap has a collision mesh
		[HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.SpawnZone)), HarmonyPrefix]
		private static bool SpawnZone(
			ZoneSystem __instance, ref bool __result, Vector2i zoneID, ZoneSystem.SpawnMode mode, out GameObject root)
		{
			var zonePos = __instance.GetZonePos(zoneID);

			var componentInChildren = __instance.m_zonePrefab.GetComponentInChildren<Heightmap>();
			if (!HeightmapBuilder.instance.IsTerrainReady(zonePos, componentInChildren.m_width,
					componentInChildren.m_scale,
					componentInChildren.m_isDistantLod,
					WorldGenerator.instance))
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
						ObjectPoolingPatch.DestroyOrReturnPooledObject(ObjectPoolingPatch.VegetationPoolByName, tempSpawnedObject);
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
		
		[HarmonyPatch(typeof(ClutterSystem), nameof(ClutterSystem.IsHeightmapReady)), HarmonyPostfix]
		private static void IsHeightmapReadyPatch(ClutterSystem __instance, ref bool __result)
		{
			// only change the result if it was true
			if (!__result) return;

			var mainCamera = Utils.GetMainCamera();
			__result = IsHeightmapReady(mainCamera.transform.position);
		}
		
		private static bool IsHeightmapReady(Vector3 pos)
		{
			var any = false;
			var ready = true;

			foreach (var heightmap in Heightmap.m_heightmaps)
			{
				if (heightmap.IsPointInside(pos))
				{
					any = true;
					ready = ready && HeightmapFinished.ContainsKey(heightmap) && HeightmapFinished[heightmap];
				}
			}

			return any && ready;
		}

		[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Shutdown)), HarmonyPostfix]
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

		/*private struct GenerateCollisionJob : IJobParallelForBatch
		{
			[ReadOnly] public int Width;
			[ReadOnly] public float Scale;
			
			[ReadOnly] public NativeArray<float> Heights;

			[WriteOnly] public NativeArray<Vector3> Verts;
			
			[WriteOnly] public NativeArray<float> Mins;
			[WriteOnly] public NativeArray<float> Maxs;

			public void Execute(int startIndex, int count)
			{
				var w1 = Width + 1;
				var maxHeight = -999999f;
				var minHeight = 999999f;
				
				var end = startIndex + count;
				var batchIndex = (end - 1) / w1;
				for (var index = startIndex; index < end; index++)
				{
					var i = (int)math.floor((float)index / w1);
					var j = index % w1;
				
					var vtx = CalcVertex(j, i, w1);
					if (vtx.y > maxHeight)
					{
						maxHeight = vtx.y;
					}
					if (vtx.y < minHeight)
					{
						minHeight = vtx.y;
					}

					Verts[index] = vtx;
				}

				Mins[batchIndex] = minHeight;
				Maxs[batchIndex] = maxHeight;
			}
			
			private Vector3 CalcVertex(int x, int y, int w1)
			{
				return new Vector3(Width * Scale * -0.5f, 0f, Width * Scale * -0.5f) 
					+ new Vector3(y: Heights[y * w1 + x], x: x * Scale, z: y * Scale);
			}
		}*/
	}
}
