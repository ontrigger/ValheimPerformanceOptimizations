using System;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace ValheimPerformanceOptimizations
{
    [HarmonyPatch]
    internal static class ModCompatibility
    {
        internal static event Action JotunnPrefabsRegisteredEvent;

        internal static bool IsJotunnPresent => 
            Chainloader.PluginInfos.ContainsKey(ValheimPerformanceOptimizations.JotunnId);
        
        internal static bool IsValheimRaftPresent => 
            Chainloader.PluginInfos.ContainsKey(ValheimPerformanceOptimizations.ValheimRaftId);
        
        internal static void Initialize()
        {
            if (IsJotunnPresent)
            {
                InitJotunnCompat();
            }
        }
        
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        private static void InitJotunnCompat()
        {
            Jotunn.Managers.PrefabManager.OnPrefabsRegistered +=
                () => JotunnPrefabsRegisteredEvent?.Invoke();
        }
    }
}