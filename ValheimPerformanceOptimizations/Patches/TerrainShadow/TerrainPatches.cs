using UnityEngine;
using UnityEngine.Rendering;

namespace ValheimPerformanceOptimizations.Patches
{
	// TODO: unused for now
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

			shadowPassCommandBuffer = new CommandBuffer { name = "Last cascade terrain pass" };

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
			if (shadowCascadeCount == cascadeCount)
			{
				return;
			}

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
