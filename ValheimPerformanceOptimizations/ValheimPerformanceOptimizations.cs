using System.Reflection;
using BepInEx;
using HarmonyLib;

namespace ValheimPerformanceOptimizations
{
    [BepInPlugin(PluginId, "Valheim Performance Optimizations", "0.0.1")]
    [BepInDependency("moreslots", BepInDependency.DependencyFlags.SoftDependency)]
    public class ValheimPerformanceOptimizations : BaseUnityPlugin
    {
        public const string PluginId = "dev.ontrigger.vpo";

        private static ValheimPerformanceOptimizations _instance;
        private Harmony _harmony;

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