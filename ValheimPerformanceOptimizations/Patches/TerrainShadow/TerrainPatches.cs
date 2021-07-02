using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace ValheimPerformanceOptimizations.Patches
{
    /// <summary>
    ///     The large terrain lod is being rendered into the shadowmap for all shadowed lights
    ///     yet doesn't survive the depth test, making it an entirely useless computation
    ///     This patch simply disables shadow casting for the terrain lod.
    /// </summary>
    [HarmonyPatch]
    public static class LargeTerrainShadowPatch
    {
        private static readonly int ClearedMaskTex = Shader.PropertyToID("_ClearedMaskTex");

        [HarmonyPatch(typeof(Heightmap), "Render")]
        private static bool Prefix(Heightmap __instance)
        {
            if (!__instance.IsVisible()) return false;

            if (__instance.m_dirty)
            {
                __instance.m_dirty = false;
                __instance.m_materialInstance.SetTexture(ClearedMaskTex, __instance.m_paintMask);
                __instance.RebuildRenderMesh();
            }

            if (__instance.m_renderMesh)
            {
                var matrix = Matrix4x4.TRS(__instance.transform.position, Quaternion.identity, Vector3.one);

                var shadowCastingMode = __instance.m_isDistantLod ? ShadowCastingMode.Off : ShadowCastingMode.On;

                Graphics.DrawMesh(__instance.m_renderMesh, matrix, __instance.m_materialInstance,
                                  __instance.gameObject.layer, null, 0,
                                  null, shadowCastingMode);
            }

            return false;
        }
    }

    // TODO: this can be reused to render lower LODs for vegetation, like trees
    public class VPOTerrainLodShadowRenderer : MonoBehaviour
    {
        public const int TerrainLodLayer = 30;

        public static VPOTerrainLodShadowRenderer Instance;

        private Light directionalLight;

        private Heightmap heightmap;

        private int shadowCascadeCount = -1;
        private int shadowCasterPass = -1;

        private CommandBuffer shadowPassCommandBuffer;

        private Matrix4x4 terrainTransform;

        private void Awake()
        {
            Instance = this;

            shadowPassCommandBuffer = new CommandBuffer {name = "Last cascade terrain pass"};

            directionalLight = GetComponent<Light>();
            //directionalLight.cullingMask &= ~(1 << TerrainLodLayer);

            var gameMain = GameObject.Find("_GameMain");

            var terrainLod = gameMain.transform.Find("Terrain_lod");

            terrainLod.gameObject.layer = TerrainLodLayer;

            heightmap = terrainLod.GetComponent<Heightmap>();

            //SetShadowCascadeCount(QualitySettings.shadowCascades);
        }

        public void HeightmapChanged()
        {
            ValheimPerformanceOptimizations.Logger.LogInfo("HEIGHTMAP CHANGED");
            UpdateCommandBuffer();
            SetShadowCascadeCount(QualitySettings.shadowCascades);
        }

        public void SetShadowCascadeCount(int cascadeCount)
        {
            if (shadowCascadeCount == cascadeCount) return;

            shadowCascadeCount = QualitySettings.shadowCascades;

            directionalLight.RemoveCommandBuffer(LightEvent.BeforeShadowMapPass, shadowPassCommandBuffer);

            directionalLight.AddCommandBuffer(
                LightEvent.BeforeShadowMapPass,
                shadowPassCommandBuffer,
                GetShadowMapPass(shadowCascadeCount)
            );
        }

        private void UpdateCommandBuffer()
        {
            if (shadowCasterPass == -1)
            {
                shadowCasterPass = heightmap.m_materialInstance.FindPass("ShadowCaster");
            }

            terrainTransform.SetTRS(heightmap.transform.position, Quaternion.identity, Vector3.one);

            shadowPassCommandBuffer.Clear();
            //shadowPassCommandBuffer.SetRenderTarget(BuiltinRenderTextureType.CurrentActive);

            shadowPassCommandBuffer.DrawMesh(
                heightmap.m_renderMesh, terrainTransform,
                heightmap.m_materialInstance, 0, shadowCasterPass);
        }

        private static ShadowMapPass GetShadowMapPass(int shadowCascadeCount)
        {
            switch (shadowCascadeCount)
            {
                case 2:
                    return ShadowMapPass.DirectionalCascade1;
                case 3:
                    return ShadowMapPass.DirectionalCascade2;
                case 4:
                    return ShadowMapPass.DirectionalCascade3;
                default:
                    return ShadowMapPass.DirectionalCascade3;
            }
        }
    }
}