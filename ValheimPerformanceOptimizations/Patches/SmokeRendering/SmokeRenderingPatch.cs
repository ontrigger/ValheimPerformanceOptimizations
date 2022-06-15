using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
namespace ValheimPerformanceOptimizations.Patches.SmokeRendering
{
    /// <summary>
    /// The smoke particles can be rendered up to 8 times per instance due to its shader
    /// doing per-pixel lighting for each light in the scene.
    /// This patch sets the particle material to use a modified Lux shader instead,
    /// which is vertex-lit by default and is rendered instanced in one pass.
    /// Rendering everything in one drawcall does not produce correct results so I batch draws by emitter
    /// </summary>
    public static class SmokeRenderingPatch
    {
        private const string AssetBundleName = "smoke_instanced_shader";
        private const string ShaderAssetPath = "Assets/Shaders/Lux Lit Smoke Instanced.shader";

        public static Shader SmokeShader;

        private static ConfigEntry<bool> _instancedSmokeRenderingEnabled;

        static SmokeRenderingPatch()
        {
            ValheimPerformanceOptimizations.OnInitialized += Initialize;
        }

        private static void Initialize(ConfigFile configFile, Harmony harmony)
        {
            const string key = "Instanced smoke rendering enabled";
			const string description = "Experimental: if enabled, smoke puffs will be rendered much faster than before.";
            _instancedSmokeRenderingEnabled = configFile.Bind("General", key, true, description);

            if (_instancedSmokeRenderingEnabled.Value)
            {
                harmony.PatchAll(typeof(SmokeRenderingPatch));
                
                var assetBundle = AssetBundleHelper.GetAssetBundleFromResources(AssetBundleName);
                SmokeShader = assetBundle.LoadAsset<Shader>(ShaderAssetPath);
            }
        }
        
        [HarmonyPatch(typeof(Smoke), nameof(Smoke.Awake)), HarmonyPrefix]
        private static bool Smoke_Awake_Prefix(Smoke __instance)
        {
            __instance.m_body = __instance.GetComponent<Rigidbody>();

            Smoke.m_smoke.Add(__instance);
            __instance.m_added = true;
            __instance.m_mr = __instance.GetComponent<MeshRenderer>();
            __instance.m_vel += Quaternion.Euler(0f, Random.Range(0, 360), 0f) * Vector3.forward *
                                __instance.m_randomVel;

            Object.Destroy(__instance.m_mr);

            return false;
        }
		
        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake)), HarmonyPostfix]
        private static void ZNetScene_Awake_Postfix(ZNetScene __instance)
        {
			__instance.gameObject.AddComponent<VPOSmokeRenderer>();
        }

        [HarmonyPatch(typeof(SmokeSpawner), nameof(SmokeSpawner.Start)), HarmonyPrefix]
        private static bool SmokeSpawner_Start_Prefix(SmokeSpawner __instance)
        {
            VPOSmokeSpawner.SmokePrefab = __instance.m_smokePrefab;
            
            var newSmokeSpawner = __instance.gameObject.AddComponent<VPOSmokeSpawner>();
            newSmokeSpawner.m_testRadius = __instance.m_testRadius;
            newSmokeSpawner.m_testMask = __instance.m_testMask;
            newSmokeSpawner.m_interval = __instance.m_interval;

            Object.Destroy(__instance);

            return false;
        }

        [HarmonyPatch(typeof(Smoke), nameof(Smoke.OnDestroy)), HarmonyPrefix]
        private static bool Smoke_OnDestroy_Prefix(Smoke __instance)
        {
            Smoke.m_smoke.Remove(__instance);

            if (__instance.m_added)
            {
                __instance.m_added = false;
            }

            return false;
        }

        [HarmonyPatch(typeof(Smoke), nameof(Smoke.StartFadeOut)), HarmonyPrefix]
        private static bool Smoke_StartFadeOut_Prefix(Smoke __instance)
        {
            if (!(__instance.m_fadeTimer >= 0f))
            {
                if (__instance.m_added)
                {
                    __instance.m_added = false;
                }

                __instance.m_fadeTimer = 0f;
            }

            return false;
        }

        [HarmonyPatch(typeof(Smoke), nameof(Smoke.FadeOldest)), HarmonyPrefix]
        private static bool FadeOldest(Smoke __instance)
        {
            if (Smoke.m_smoke.Count == 0) return false;

            var i = 0;
            do
            {
                var smoke = Smoke.m_smoke[i];
                if (smoke.m_added && smoke.m_fadeTimer < 0)
                {
                    smoke.StartFadeOut();
                    break;
                }

                i += 1;
            } while (i < Smoke.m_smoke.Count);

            return false;
        }


        [HarmonyPatch(typeof(Smoke), nameof(Smoke.Update)), HarmonyPrefix]
        private static bool Smoke_Update_Prefix(Smoke __instance)
        {
            __instance.m_time += Time.deltaTime;

            var num = 1f - Mathf.Clamp01(__instance.m_time / __instance.m_ttl);
            __instance.m_body.mass = num * num;
            var velocity = __instance.m_body.velocity;
            var vel = __instance.m_vel;
            vel.y *= num;
            var vector = vel - velocity;
            __instance.m_body.AddForce(vector * __instance.m_force * Time.deltaTime, ForceMode.VelocityChange);

            return false;
        }
    }
}