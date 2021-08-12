using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ValheimPerformanceOptimizations.Patches;

namespace ValheimPerformanceOptimizations
{
    [BepInDependency(ValheimRaftId, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInPlugin(PluginId, "Valheim Performance Optimizations", "0.6.1")]
    public class ValheimPerformanceOptimizations : BaseUnityPlugin
    {
        public const string PluginId = "dev.ontrigger.vpo";

        public static event Action<ConfigFile, Harmony> OnInitialized;

        internal const string ValheimRaftId = "BepIn.Sarcen.ValheimRAFT";

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

            AccessTools.GetTypesFromAssembly(Assembly.GetExecutingAssembly()).Do(type =>
            {
                // check if class is static
                if (!type.IsAbstract || !type.IsSealed) return;

                var constructor = type.GetConstructor(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, Array.Empty<ParameterModifier>()
                );

                if (constructor != null)
                {
                    RuntimeHelpers.RunClassConstructor(type.TypeHandle);
                }
            });

            OnInitialized?.Invoke(Config, _harmony);
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchAll(PluginId);
        }
    }
}