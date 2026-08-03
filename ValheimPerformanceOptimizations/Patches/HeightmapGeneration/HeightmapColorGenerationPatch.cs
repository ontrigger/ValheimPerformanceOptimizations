using System.Collections.Generic;
using HarmonyLib;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using VPOBurst;

namespace ValheimPerformanceOptimizations.Patches.HeightmapGeneration
{
	/// <summary>
	/// Remove pointless Color[32x32]/ToArray() allocations
	/// </summary>
	[HarmonyPatch]
	public class HeightmapColorGenerationPatch
	{
		private static List<Vector2> _heightmapUVs;
		private static List<Vector2> _distantHeightmapUVs;

		private static NativeArray<Color32> _heightmapColors;
		private static NativeArray<Color32> _distantHeightmapColors;

		private static readonly Queue<Mesh> RegenerateTangentQueue = new();

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

				if (_lastDistantHeightmapWidth != width)
				{
					if (_distantHeightmapColors.IsCreated)
					{
						_distantHeightmapColors.Dispose();
					}

					var w1 = width + 1;
					_distantHeightmapColors = new NativeArray<Color32>(w1 * w1, Allocator.Persistent);
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

			Profiler.BeginSample("generatin shit");

			ReallocateArrays(__instance);

			if (!isDistant)
			{
				var cornerBiomes = new NativeArray<int>(4, Allocator.TempJob);
				for (var i = 0; i < 4; i++)
				{
					cornerBiomes[i] = (int)__instance.m_cornerBiomes[i];
				}

				var job = new GenerateColorsJob
				{
					Width = width,
					CornerBiomes = cornerBiomes,
					Colors = _heightmapColors,
				};

				job.Schedule(_heightmapColors.Length, __instance.m_width + 1).Complete();
			}
			else
			{
				var num = width + 1;
				var vector = __instance.transform.position
					+ new Vector3(width * scale * -0.5f, 0f, width * scale * -0.5f);
				for (var idx = 0; idx < num * num; idx++)
				{
					var i = idx / num;
					var j = idx % num;

					var wx = vector.x + j * scale;
					var wy = vector.z + i * scale;
					var biome = worldGen.GetBiome(wx, wy);

					_distantHeightmapColors[idx] = Heightmap.GetBiomeColor(biome);
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
				__instance.m_renderMesh.SetColors(isDistant ? _distantHeightmapColors : _heightmapColors);
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

		[HarmonyPatch(typeof(Heightmap), nameof(Heightmap.CustomLateUpdate))] 
		[HarmonyPostfix]
		private static void CustomLateUpdatePostfix()
		{
			if (RegenerateTangentQueue.Count == 0) { return; }

			var mesh = RegenerateTangentQueue.Dequeue();
			while (mesh == null && RegenerateTangentQueue.Count > 0)
			{
				mesh = RegenerateTangentQueue.Dequeue();
			}

			if (mesh != null)
			{
				mesh.RecalculateTangents(~MeshUpdateFlags.Default);
			}
		}

		[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Shutdown))] [HarmonyPostfix]
		private static void ZNetSceneShutdownPostfix()
		{
			if (_heightmapColors.IsCreated)
			{
				_heightmapColors.Dispose();
			}

			if (_distantHeightmapColors.IsCreated)
			{
				_distantHeightmapColors.Dispose();
			}

			_heightmapUVs = null;
			_distantHeightmapUVs = null;
			_lastHeightmapWidth = -1;
			_lastDistantHeightmapWidth = -1;
			RegenerateTangentQueue.Clear();
		}
	}
}
