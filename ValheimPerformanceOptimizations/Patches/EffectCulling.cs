using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches;

[HarmonyPatch]
public static class EffectCulling
{
	// particles render beyond vanilla distance
	private const float EffectCullDistance = 288f;

	[HarmonyPatch(typeof(GameCamera), nameof(GameCamera.Awake))]
	[HarmonyPostfix]
	private static void GameCamera_Awake_Postfix(GameCamera __instance)
	{
		var camera = __instance.m_camera;
		if (!camera)
		{
			return;
		}

		var distances = camera.layerCullDistances;

		distances[LayerMask.NameToLayer("TransparentFX")] = EffectCullDistance;
		distances[LayerMask.NameToLayer("effect")] = EffectCullDistance;
		
		camera.layerCullDistances = distances;
		camera.layerCullSpherical = true;
	}
}
