using System.Collections.Generic;
using HarmonyLib;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
namespace ValheimPerformanceOptimizations.Patches.HeightmapGeneration
{
	/// <summary>
	/// Remove pointless Color[32x32]/ToArray() allocations
	/// </summary>
	[HarmonyPatch]
	public class HeightmapColorAllocationPatch
	{
		private static Color32[] _clearColors;
		private static Color32[] _distantClearColors;

		private static List<Vector2> _heightmapUVs;
		private static List<Vector2> _distantHeightmapUVs;

		private static NativeArray<Color32> _heightmapColors;
		private static NativeArray<Color32> _distantHeightmapColors;

		private static readonly Queue<Mesh> RegenerateTangentQueue = new Queue<Mesh>();

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
				if (_distantClearColors == null || _lastDistantHeightmapWidth != width)
				{
					_distantClearColors = new Color32[width * width];
				}

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
				if (_clearColors == null || _lastHeightmapWidth != width)
				{
					_clearColors = new Color32[width * width];
				}

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

		[HarmonyPatch(typeof(Heightmap), nameof(Heightmap.RebuildRenderMesh)), HarmonyPrefix]
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

			Heightmap.m_tempVertises.Clear();
			Heightmap.m_tempIndices.Clear();

			var width = __instance.m_width;
			var scale = __instance.m_scale;

			var isDistant = __instance.IsDistantLod;

			Profiler.BeginSample("generatin shit");

			if (!isDistant)
			{
				var job = new GenerateColorsJob
				{
					Width = width,
					CornerBiomes = new NativeArray<Heightmap.Biome>(__instance.m_cornerBiomes, Allocator.TempJob),
					Colors = _heightmapColors,
				};

				job.Schedule(_heightmapColors.Length, __instance.m_width + 1).Complete();
			}
			else
			{
				ReallocateArrays(__instance);
				
				var num = width + 1;
				var vector = __instance.transform.position + new Vector3(width * scale * -0.5f, 0f, width * scale * -0.5f);
				for (var idx = 0; idx < num * num; idx++)
				{
					var w1 = width - 1;
					var i = math.floor(idx / (float)w1);

					var wx = vector.x + idx % w1 * scale;
					var wy = vector.z + i * scale;
					var biome = worldGen.GetBiome(wx, wy);

					_distantHeightmapColors[idx] = Heightmap.GetBiomeColor(biome);
				}
			}

			Profiler.EndSample();

			Profiler.BeginSample("get stuff from col");
			__instance.m_collisionMesh.GetVertices(Heightmap.m_tempVertises);
			__instance.m_collisionMesh.GetIndices(Heightmap.m_tempIndices, 0);
			Profiler.EndSample();

			Profiler.BeginSample("settin shit");
			{
				Profiler.BeginSample("actual set");
				__instance.m_renderMesh.SetVertices(Heightmap.m_tempVertises);
				__instance.m_renderMesh.SetColors(isDistant ? _distantHeightmapColors : _heightmapColors);
				Profiler.EndSample();

				if (newMesh)
				{
					Profiler.BeginSample("set stale stuffs");
					__instance.m_renderMesh.SetUVs(0, isDistant ? _distantHeightmapUVs : _heightmapUVs);
					__instance.m_renderMesh.SetIndices(Heightmap.m_tempIndices, MeshTopology.Triangles, 0, true);
					Profiler.EndSample();
				}

				Profiler.BeginSample("recalc");
				__instance.m_renderMesh.RecalculateNormals();
				
				RegenerateTangentQueue.Enqueue(__instance.m_renderMesh);

				var mesh = RegenerateTangentQueue.Dequeue();
				while (mesh == null && RegenerateTangentQueue.Count > 0)
				{
					mesh = RegenerateTangentQueue.Dequeue();
				}

				if (mesh != null)
				{
					mesh.RecalculateTangents();
				}

				Profiler.EndSample();
			}
			Profiler.EndSample();

			return false;
		}

		/*[HarmonyPatch(typeof(Heightmap), nameof(Heightmap.Generate)), HarmonyPrefix]
		public static bool Prefix(Heightmap __instance)
		{
			Profiler.BeginSample("heightmap init");
			__instance.Initialize();
			Profiler.EndSample();
			var num = __instance.m_width + 1;
			var num2 = num * num;
			var position = __instance.transform.position;
			if (__instance.m_buildData == null ||
				__instance.m_buildData.m_baseHeights.Count != num2 ||
				__instance.m_buildData.m_center != position ||
				__instance.m_buildData.m_scale != __instance.m_scale ||
				__instance.m_buildData.m_worldGen != WorldGenerator.instance)
			{
				Profiler.BeginSample("heightmap sync wait");
				__instance.m_buildData = HeightmapBuilder.instance.RequestTerrainSync(
					position, __instance.m_width, __instance.m_scale, __instance.m_isDistantLod,
					WorldGenerator.instance);
				__instance.m_cornerBiomes = __instance.m_buildData.m_cornerBiomes;
				Profiler.EndSample();
			}

			Profiler.BeginSample("copy");
			for (var i = 0; i < num2; i++)
			{
				__instance.m_heights[i] = __instance.m_buildData.m_baseHeights[i];
			}
			Profiler.EndSample();

			Profiler.BeginSample("setpixels");
			// the only change
			__instance.m_paintMask.SetPixels32(__instance.m_isDistantLod ? _distantClearColors : _clearColors);
			Profiler.EndSample();

			Profiler.BeginSample("heightmap modifiers");
			__instance.ApplyModifiers();
			Profiler.EndSample();

			return false;
		}*/

		private struct GenerateColorsJob : IJobParallelFor
		{
			[ReadOnly] public int Width;

			[ReadOnly, DeallocateOnJobCompletion] 
			public NativeArray<Heightmap.Biome> CornerBiomes;

			[WriteOnly] public NativeArray<Color32> Colors;

			public void Execute(int index)
			{
				var w1 = Width + 1;

				var i = math.floor(index / (float)w1);
				var j = index % w1;

				var iy = math.smoothstep(0f, 1f, i / Width);
				var ix = math.smoothstep(0f, 1f, j / (float)Width);

				Colors[index] = GetBiomeColor(ix, iy);
			}

			private Color GetBiomeColor(float ix, float iy)
			{
				if ((CornerBiomes[0] | CornerBiomes[1] | CornerBiomes[2] | CornerBiomes[3]) == CornerBiomes[0])
				{
					return GetBiomeColor(CornerBiomes[0]);
				}
				var biomeColor = GetBiomeColor(CornerBiomes[0]);
				var biomeColor2 = GetBiomeColor(CornerBiomes[1]);
				var biomeColor3 = GetBiomeColor(CornerBiomes[2]);
				var biomeColor4 = GetBiomeColor(CornerBiomes[3]);

				var a = Color32.Lerp(biomeColor, biomeColor2, ix);
				var b = Color32.Lerp(biomeColor3, biomeColor4, ix);

				return Color32.Lerp(a, b, iy);
			}

			private Color32 GetBiomeColor(Heightmap.Biome biome)
			{
				return biome switch
				{
					Heightmap.Biome.Swamp => new Color32(byte.MaxValue, 0, 0, 0),
					Heightmap.Biome.Mountain => new Color32(0, byte.MaxValue, 0, 0),
					Heightmap.Biome.BlackForest => new Color32(0, 0, byte.MaxValue, 0),
					Heightmap.Biome.Plains => new Color32(0, 0, 0, byte.MaxValue),
					Heightmap.Biome.AshLands => new Color32(byte.MaxValue, 0, 0, byte.MaxValue),
					Heightmap.Biome.DeepNorth => new Color32(0, byte.MaxValue, 0, 0),
					Heightmap.Biome.Mistlands => new Color32(0, 0, byte.MaxValue, byte.MaxValue),
					_ => new Color32(0, 0, 0, 0)
				};
			}
		}
	}
}
