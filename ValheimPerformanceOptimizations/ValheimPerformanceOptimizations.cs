using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations
{
    [BepInPlugin(PluginId, "Valheim Performance Optimizations", "0.0.1")]
    public class ValheimPerformanceOptimizations : BaseUnityPlugin
    {
        public const string PluginId = "dev.ontrigger.vpo";

        private static ValheimPerformanceOptimizations _instance;
        private Harmony _harmony;
        
        internal new static ManualLogSource Logger { get; private set; }

        private ValheimPerformanceOptimizations()
        {
            Logger = base.Logger;
        }

        private void Awake()
        {
            _instance = this;

            _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), PluginId);
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchAll(PluginId);
        }
    }
}