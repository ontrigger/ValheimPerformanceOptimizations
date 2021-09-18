using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Utils;

namespace ValheimPerformanceOptimizations
{
    [BepInDependency(ValheimRaftId, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(JotunnId, BepInDependency.DependencyFlags.SoftDependency)]
    [NetworkCompatibility(CompatibilityLevel.NoNeedForSync, VersionStrictness.None)]
    [BepInPlugin(PluginId, "Valheim Performance Optimizations", PluginVersion)]
    public class ValheimPerformanceOptimizations : BaseUnityPlugin
    {
        public const string PluginId = "dev.ontrigger.vpo";
        public const string PluginVersion = "0.7.4";

        public static event Action<ConfigFile, Harmony> OnInitialized;

        internal const string ValheimRaftId = "BepIn.Sarcen.ValheimRAFT";
        internal const string JotunnId = "com.jotunn.jotunn";

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

            ModCompatibility.Initialize();
            OnInitialized?.Invoke(Config, _harmony);
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchAll(PluginId);
        }
    }
}