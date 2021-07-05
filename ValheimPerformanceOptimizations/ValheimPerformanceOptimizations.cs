using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using ValheimPerformanceOptimizations.Patches;

namespace ValheimPerformanceOptimizations
{
    [BepInPlugin(PluginId, "Valheim Performance Optimizations PREVIEW", "0.5.0")]
    public class ValheimPerformanceOptimizations : BaseUnityPlugin
    {
        public const string PluginId = "dev.ontrigger.vpo";

        private static ValheimPerformanceOptimizations _instance;
        private Harmony _harmony;
        
        private ValheimPerformanceOptimizations()
        {
            Logger = base.Logger;
        }

        internal new static ManualLogSource Logger { get; private set; }

        private void Awake()
        {
            _instance = this;

            _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), PluginId);
            
            ZoneSystemObjectPoolingPatch.Initialize(Config, _harmony);
            ThreadedHeightmapCollisionBakePatch.Initialize(Config, _harmony);
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchAll(PluginId);
        }
    }
}