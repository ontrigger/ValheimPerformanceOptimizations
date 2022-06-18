using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches
{
	/// <summary>
	/// Physics can update up to 15 times per frame by default.
	/// Unfortunately phys updates take up most of the frametime in the profiler
	/// This patch reduces the maximum amount of phys updates to the configured value
	/// </summary>
	public static class MaxPhysicsTimeStepPatch
	{
		private static ConfigEntry<int> _maxUpdatesPerFrame;

		static MaxPhysicsTimeStepPatch()
		{
			ValheimPerformanceOptimizations.OnInitialized += Initialize;
		}

		private static void Initialize(ConfigFile configFile, Harmony harmony)
		{
			const string key = "Max physics updates per frame";
			const string description
				= "The engine updates physics multiple times per frame. \n" +
				"Coincidentally, physics calculation is the most expensive part of Valheim and it can be done up to 15 times per frame. \n" +
				"If can't get more than 20 fps, reducing this value can significantly boost fps in bases at the cost of less accurate physics. \n" +
				"The lowest you can go is 5";

			var valueConfig = new ConfigDescription(description, new AcceptableValueRange<int>(5, 15));
			_maxUpdatesPerFrame = configFile.Bind("General", key, 8, valueConfig);

			harmony.PatchAll(typeof(MaxPhysicsTimeStepPatch));
		}

		[HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.Awake)), HarmonyPostfix]
		private static void Postfix()
		{
			Time.maximumDeltaTime = _maxUpdatesPerFrame.Value * Time.fixedDeltaTime;
		}
	}
}
