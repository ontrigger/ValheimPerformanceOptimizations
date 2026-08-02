using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Unity.Mathematics;
using UnityEngine;
using VPOBurst;

namespace ValheimPerformanceOptimizations.Patches.Water
{
	[HarmonyPatch]
	public static class BurstedCalcWavePatch
	{
		private static readonly MethodInfo VanillaCalcWave = AccessTools.Method(
			typeof(WaterVolume), nameof(WaterVolume.CalcWave),
			[typeof(Vector3), typeof(float), typeof(float), typeof(float)]);

		private static readonly MethodInfo BurstedCalcWave = AccessTools.Method(
			typeof(BurstedCalcWavePatch), nameof(CalcWaveBursted));

		private static IEnumerable<MethodBase> TargetMethods()
		{
			yield return AccessTools.Method(typeof(Fish), nameof(Fish.Start));
			yield return AccessTools.Method(typeof(Fish), nameof(Fish.CustomFixedUpdate));
			yield return AccessTools.Method(typeof(WaterVolume), nameof(WaterVolume.GetWaterSurface));
		}

		[HarmonyTranspiler]
		private static IEnumerable<CodeInstruction> ReplaceCalcWave(IEnumerable<CodeInstruction> instructions)
		{
			foreach (var instruction in instructions)
			{
				if (instruction.Calls(VanillaCalcWave))
				{
					yield return new CodeInstruction(OpCodes.Call, BurstedCalcWave);
				}
				else
				{
					yield return instruction;
				}
			}
		}

		public static float CalcWaveBursted(
			WaterVolume _, Vector3 worldPos, float depth, float waterTime, float waveFactor)
		{
			var pos = (float3)worldPos;
			var wind1 = (float4)WaterVolume.s_globalWind1;
			var wind2 = (float4)WaterVolume.s_globalWind2;
			return WaterWaves.CalcWaveBlended(
				in pos, depth, in wind1, in wind2, WaterVolume.s_globalWindAlpha, waterTime, waveFactor);
		}
	}
}
