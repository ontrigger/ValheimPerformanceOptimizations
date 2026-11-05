using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using ValheimPerformanceOptimizations.Burst;

namespace ValheimPerformanceOptimizations.Patches.HeightmapGeneration;

/// <summary>
/// Remove pointless Color[32x32]/ToArray() allocations
/// Add burst-optimized color generation
/// Regenerate fixed amount of tangents per frame
/// </summary>
public class HeightmapColorGenerationPatch
{
	private static List<Vector2> _heightmapUVs;
	private static List<Vector2> _distantHeightmapUVs;

	private static NativeArray<Color32> _heightmapColors;
	private static Color32[] _distantHeightmapColors;

	private static readonly ConditionalWeakTable<HeightmapBuilder.HMBuildData, Color32[]> DistantColorsByBuildData
		= new();

	private static readonly Queue<Mesh> RegenerateTangentQueue = new();
	private const int MaxTangentsPerFrame = 2;
	private static int _tangentRegenerationFrame = -1;
	private static int _tangentsRegeneratedThisFrame;

	private static int _lastHeightmapWidth = -1;
	private static int _lastDistantHeightmapWidth = -1;

	[HarmonyPatch(typeof(Heightmap), nameof(Heightmap.Awake))]
	public static void Postfix(Heightmap __instance)
	{
		ReallocateArrays(__instance);
	}

	private static void ReallocateArrays(Heightmap __instance)
	{
		var width = __instance.m_width;

		if (__instance.IsDistantLod)
		{
			if (_distantHeightmapUVs == null || _lastDistantHeightmapWidth != width)
			{
				var num = width + 1;

				var uvs = new List<Vector2>();
				for (var i = 0; i < num; i++)
				{
					for (var j = 0; j < num; j++)
					{
						uvs.Add(new Vector2(j / (float)width, i / (float)width));
					}
				}

				_distantHeightmapUVs = uvs;
			}

			if (_distantHeightmapColors == null || _lastDistantHeightmapWidth != width)
			{
				var w1 = width + 1;
				_distantHeightmapColors = new Color32[w1 * w1];
			}

			_lastDistantHeightmapWidth = width;
		}
		else
		{
			if (_heightmapUVs == null || _lastHeightmapWidth != width)
			{
				var num = width + 1;

				var uvs = new List<Vector2>();
				for (var i = 0; i < num; i++)
				{
					for (var j = 0; j < num; j++)
					{
						uvs.Add(new Vector2(j / (float)width, i / (float)width));
					}
				}

				_heightmapUVs = uvs;
			}

			if (_lastHeightmapWidth != width)
			{
				if (_heightmapColors.IsCreated)
				{
					_heightmapColors.Dispose();
				}

				var w1 = width + 1;
				_heightmapColors = new NativeArray<Color32>(w1 * w1, Allocator.Persistent);
			}

			_lastHeightmapWidth = width;
		}
	}

	[HarmonyPatch(typeof(Heightmap), nameof(Heightmap.RebuildRenderMesh))] [HarmonyPrefix]
	public static bool RebuildRenderMeshPostfix(Heightmap __instance)
	{
		var newMesh = false;
		if (__instance.m_renderMesh == null)
		{
			__instance.m_renderMesh = new Mesh();
			__instance.m_renderMesh.MarkDynamic();
			newMesh = true;
		}

		var worldGen = WorldGenerator.instance;

		Heightmap.s_tempVertices.Clear();
		Heightmap.s_tempIndices.Clear();

		var width = __instance.m_width;
		var scale = __instance.m_scale;

		var isDistant = __instance.IsDistantLod;
		Color32[] distantColors = _distantHeightmapColors;

		Profiler.BeginSample("generatin shit");

		ReallocateArrays(__instance);

		if (!isDistant)
		{
			var cornerColors = new NativeArray<Color32>(4, Allocator.TempJob);
			for (var i = 0; i < 4; i++)
			{
				cornerColors[i] = Heightmap.GetBiomeColor(__instance.m_cornerBiomes[i]);
			}

			var job = new GenerateColorsJob { Width = width, CornerColors = cornerColors, Colors = _heightmapColors };

			job.Schedule(_heightmapColors.Length, __instance.m_width + 1).Complete();
		}
		else
		{
			var buildData = __instance.m_buildData;
			if (buildData != null && DistantColorsByBuildData.TryGetValue(buildData, out Color32[] prebuilt))
			{
				distantColors = prebuilt;
			}
			else
			{
				distantColors = _distantHeightmapColors;
				GenerateDistantColors(__instance.transform.position, width, scale, worldGen, distantColors);
			}
		}

		Profiler.EndSample();

		Profiler.BeginSample("get stuff from col");
		__instance.m_collisionMesh.GetVertices(Heightmap.s_tempVertices);
		__instance.m_collisionMesh.GetIndices(Heightmap.s_tempIndices, 0);
		Profiler.EndSample();

		Profiler.BeginSample("settin shit");
		{
			Profiler.BeginSample("actual set");
			__instance.m_renderMesh.SetVertices(Heightmap.s_tempVertices);
			if (isDistant)
			{
				__instance.m_renderMesh.SetColors(distantColors);
			}
			else
			{
				__instance.m_renderMesh.SetColors(_heightmapColors);
			}
			Profiler.EndSample();

			if (newMesh)
			{
				Profiler.BeginSample("set stale stuffs");
				__instance.m_renderMesh.SetUVs(0, isDistant ? _distantHeightmapUVs : _heightmapUVs);
				__instance.m_renderMesh.SetIndices(Heightmap.s_tempIndices, MeshTopology.Triangles, 0);
				Profiler.EndSample();
			}

			Profiler.BeginSample("recalc");
			__instance.m_renderMesh.RecalculateNormals();

			RegenerateTangentQueue.Enqueue(__instance.m_renderMesh);

			__instance.m_renderMesh.RecalculateBounds();
			__instance.m_meshFilter.mesh = __instance.m_renderMesh;

			Profiler.EndSample();
		}
		Profiler.EndSample();

		return false;
	}

	// runs on the HeightmapBuilder thread.
	[HarmonyPatch(typeof(HeightmapBuilder), nameof(HeightmapBuilder.Build))]
	[HarmonyPostfix]
	private static void HeightmapBuilderBuildPostfix(HeightmapBuilder.HMBuildData data)
	{
		if (!data.m_distantLod)
		{
			return;
		}

		var num = data.m_width + 1;
		var colors = new Color32[num * num];
		GenerateDistantColors(data.m_center, data.m_width, data.m_scale, data.m_worldGen, colors);

		DistantColorsByBuildData.Remove(data);
		DistantColorsByBuildData.Add(data, colors);
	}

	private static void GenerateDistantColors(
		Vector3 center, int width, float scale, WorldGenerator worldGen, Color32[] colors)
	{
		var num = width + 1;
		var originX = center.x + width * scale * -0.5f;
		var originZ = center.z + width * scale * -0.5f;

		BiomeSector lastSector = null;
		var lastColor = default(Color32);

		var idx = 0;
		for (var i = 0; i < num; i += 1)
		{
			var gridY = AltBiomeWorldData.WorldSpaceToMapSpace(originZ + i * scale);
			for (var j = 0; j < num; j += 1)
			{
				var gridX = AltBiomeWorldData.WorldSpaceToMapSpace(originX + j * scale);
				var sector = worldGen.GetBiomeSector(gridX, gridY);

				if (sector != lastSector)
				{
					lastColor = Heightmap.GetBiomeColor(sector);
					lastSector = sector;
				}

				colors[idx] = lastColor;
				idx += 1;
			}
		}
	}

	[HarmonyPatch(typeof(Heightmap), nameof(Heightmap.CustomLateUpdate))]
	[HarmonyPostfix]
	private static void CustomLateUpdatePostfix()
	{
		if (_tangentRegenerationFrame != Time.frameCount)
		{
			_tangentRegenerationFrame = Time.frameCount;
			_tangentsRegeneratedThisFrame = 0;
		}

		if (_tangentsRegeneratedThisFrame >= MaxTangentsPerFrame || RegenerateTangentQueue.Count == 0)
		{
			return;
		}

		Profiler.BeginSample("Regenerate tangents");
		var mesh = RegenerateTangentQueue.Dequeue();
		while (mesh == null && RegenerateTangentQueue.Count > 0)
		{
			mesh = RegenerateTangentQueue.Dequeue();
		}

		if (mesh != null)
		{
			mesh.RecalculateTangents(~MeshUpdateFlags.Default);
			_tangentsRegeneratedThisFrame++;
		}
		Profiler.EndSample();
	}

	[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Shutdown))] [HarmonyPostfix]
	private static void ZNetSceneShutdownPostfix()
	{
		if (_heightmapColors.IsCreated)
		{
			_heightmapColors.Dispose();
		}

		_distantHeightmapColors = null;

		_heightmapUVs = null;
		_distantHeightmapUVs = null;
		_lastHeightmapWidth = -1;
		_lastDistantHeightmapWidth = -1;
		RegenerateTangentQueue.Clear();
	}
}
