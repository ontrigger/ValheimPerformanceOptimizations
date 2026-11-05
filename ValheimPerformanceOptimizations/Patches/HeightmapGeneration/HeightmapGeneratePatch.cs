using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;

namespace ValheimPerformanceOptimizations.Patches.HeightmapGeneration;

// bulk copy heights and preallocates arrays, saves at most 1ms
public static class HeightmapGeneratePatch
{
	[HarmonyPatch(typeof(Heightmap), nameof(Heightmap.Generate))]
	[HarmonyPrefix]
	private static bool GeneratePrefix(Heightmap __instance, ref bool __result)
	{
		if (HeightmapBuilder.instance == null)
		{
			__result = false;
			return false;
		}

		if (WorldGenerator.instance == null)
		{
			ZLog.LogError("The WorldGenerator instance was null");
			throw new NullReferenceException("The WorldGenerator instance was null");
		}

		Profiler.BeginSample(__instance.IsDistantLod ? "Heightmap.Generate (distant)" : "Heightmap.Generate");

		Profiler.BeginSample("Initialize");
		Initialize(__instance);
		Profiler.EndSample();

		var num = __instance.m_width + 1;
		var num2 = num * num;
		var position = __instance.transform.position;
		var buildData = __instance.m_buildData;
		if (buildData == null || buildData.m_baseHeights.Count != num2 || buildData.m_center != position ||
		    buildData.m_scale != __instance.m_scale || buildData.m_worldGen != WorldGenerator.instance)
		{
			buildData = HeightmapBuilder.instance.RequestTerrainSync(position, __instance.m_width,
				__instance.m_scale, __instance.m_isDistantLod, WorldGenerator.instance);
			__instance.m_buildData = buildData;

			Profiler.BeginSample("Corner biomes");
			__instance.m_cornerBiomes = buildData.m_cornerBiomes;
			foreach (var biomeSector in __instance.m_cornerBiomes)
			{
				if (!__instance.m_cornerBiomeList.Contains(biomeSector))
				{
					__instance.m_cornerBiomeList.Add(biomeSector);
				}

				foreach (var altBiome in biomeSector.AltBiomes)
				{
					if (!__instance.m_cornerAltBiomes.Contains(altBiome))
					{
						__instance.m_cornerAltBiomes.Add(altBiome);
					}
				}
			}
			Profiler.EndSample();
		}

		Profiler.BeginSample("Copy base heights");
		List<float> heights = __instance.m_heights;
		if (heights.Count == num2)
		{
			Array.Copy(
				NoAllocHelpers.ExtractArrayFromList(buildData.m_baseHeights),
				NoAllocHelpers.ExtractArrayFromList(heights),
				num2);
		}
		else
		{
			heights.Clear();
			heights.Capacity = num2;
			heights.AddRange(buildData.m_baseHeights);
		}
		Profiler.EndSample();

		Profiler.BeginSample("Paint mask SetPixels");
		__instance.m_paintMask.SetPixels(buildData.m_baseMask);
		Profiler.EndSample();

		Profiler.BeginSample("ApplyModifiers");
		__instance.ApplyModifiers();
		Profiler.EndSample();

		Profiler.EndSample();

		__result = true;
		return false;
	}

	private static void Initialize(Heightmap heightmap)
	{
		var num = heightmap.m_width + 1;
		if (heightmap.m_heights.Count == num * num)
		{
			return;
		}

		heightmap.m_paintMask = new Texture2D(num, num);
		heightmap.m_paintMask.name = "_Heightmap m_paintMask";
		heightmap.m_paintMask.wrapMode = TextureWrapMode.Clamp;

		heightmap.m_materialInstance = new Material(heightmap.m_material);
		heightmap.m_materialInstance.SetTexture(Heightmap.s_shaderPropertyClearedMaskTex, heightmap.m_paintMask);
		heightmap.m_meshRenderer.sharedMaterial = heightmap.m_materialInstance;
	}
}
