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
            const string description = "If you have low fps, the engine will update physics multiple times per frame. \n" 
                + "Coincidentally, physics calculation is the most expensive part of Valheim and it can be done up to 15(!!!) times per frame. \n"
                + "Reducing this value can significantly increase fps in bases at the cost of less accurate physics. \n" 
                + "The lowest you can go is 5";
                
            _maxUpdatesPerFrame = configFile.Bind("General", key, 6, description);
            if (_maxUpdatesPerFrame.Value < 5)
            {
                ValheimPerformanceOptimizations.Logger.LogError($"Max physics updates per frame can not be lower than 5, was: {_maxUpdatesPerFrame.Value}");
                _maxUpdatesPerFrame.Value = 6;
                configFile.Save();
            }

            harmony.PatchAll(typeof(MaxPhysicsTimeStepPatch));
        }

        [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.Awake)), HarmonyPostfix]
        private static void Postfix()
        {
            Time.maximumDeltaTime = _maxUpdatesPerFrame.Value * Time.fixedDeltaTime;
        }
    }
}