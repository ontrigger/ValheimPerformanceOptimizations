using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches.IndirectRendering;

/// <summary>
/// The point and spot lights that LightLod has given shadows. LightLod applies
/// the game's shadowed light limit by switching shadows on and off, so this
/// list follows those switches.
/// </summary>
[HarmonyPatch]
internal static class ShadowedLights
{
	internal static readonly List<Light> Active = new();

	private static readonly MethodInfo LightShadowsSetter =
		AccessTools.PropertySetter(typeof(Light), nameof(Light.shadows));

	[HarmonyPatch(typeof(LightLod), nameof(LightLod.Awake))]
	[HarmonyPostfix]
	private static void LightLodAwakePostfix(LightLod __instance)
	{
		Update(__instance.m_light);
	}

	[HarmonyPatch(typeof(LightLod), nameof(LightLod.UpdateLoop), MethodType.Enumerator)]
	[HarmonyTranspiler]
	private static IEnumerable<CodeInstruction> LightLodUpdateLoopTranspiler(IEnumerable<CodeInstruction> instructions)
	{
		foreach (var instruction in instructions)
		{
			if (instruction.Calls(LightShadowsSetter))
			{
				instruction.opcode = OpCodes.Call;
				instruction.operand = AccessTools.Method(typeof(ShadowedLights), nameof(SetShadows));
			}

			yield return instruction;
		}
	}

	[HarmonyPatch(typeof(LightLod), nameof(LightLod.OnDestroy))]
	[HarmonyPostfix]
	private static void LightLodOnDestroyPostfix(LightLod __instance)
	{
		Active.Remove(__instance.m_light);
	}

	private static void SetShadows(Light light, LightShadows shadows)
	{
		light.shadows = shadows;
		Update(light);
	}

	private static void Update(Light light)
	{
		if (!light || light.type is not (LightType.Point or LightType.Spot))
		{
			return;
		}

		if (light.shadows == LightShadows.None)
		{
			Active.Remove(light);
		}
		else if (!Active.Contains(light))
		{
			Active.Add(light);
		}
	}
}
