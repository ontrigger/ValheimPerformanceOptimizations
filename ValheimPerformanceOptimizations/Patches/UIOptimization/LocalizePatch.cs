using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ValheimPerformanceOptimizations.Patches.UIOptimization
{
	/// <summary>
	/// Localization is done every frame.
	/// Don't do it every frame.
	/// </summary>
	[HarmonyPatch]
	public static class LocalizePatch
	{
		private static readonly List<Text> TempTextComponents = new List<Text>();
		private static float _recheckTimer = 1;

		// I can't be sure that ReLocalizeVisible is called once per frame due to other mods,
		// so I put it in localize update 
		[HarmonyPatch(typeof(Localize), nameof(Localize.Update))]
		private static void Prefix(Localize __instance)
		{
			_recheckTimer -= Time.deltaTime;
		}

		[HarmonyPatch(typeof(Localization), nameof(Localization.ReLocalizeVisible))]
		private static bool Prefix(Localization __instance, Transform root)
		{
			// only check gui
			if (root.name != "IngameGui(Clone)") { return true; }

			if (_recheckTimer > 0) { return false; }
			
			root.gameObject.GetComponentsInChildren(includeInactive: true, TempTextComponents);

			// localizing once per second seems to be completely fine?
			foreach (var text in TempTextComponents)
			{
				if (text == null)
				{
					_recheckTimer = 0;
					continue;
				}

				if (text.gameObject.activeInHierarchy && __instance.textStrings.TryGetValue(text, out var value))
				{
					text.text = __instance.Localize(value);
				}
			}

			_recheckTimer = 1;

			return false;
		}
	}
}
