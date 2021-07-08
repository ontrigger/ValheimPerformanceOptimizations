using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using ValheimPerformanceOptimizations.Patches;

namespace ValheimPerformanceOptimizations
{
    [BepInDependency(ValheimRaftId, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInPlugin(PluginId, "Valheim Performance Optimizations", "0.6.0")]
    public class ValheimPerformanceOptimizations : BaseUnityPlugin
    {
        public const string PluginId = "dev.ontrigger.vpo";

        private const string ValheimRaftId = "BepIn.Sarcen.ValheimRAFT";

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

            if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(ValheimRaftId))
            {
                GetStandingOnShipPatch.Initialize(_harmony);
            }
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchAll(PluginId);
        }
    }
}