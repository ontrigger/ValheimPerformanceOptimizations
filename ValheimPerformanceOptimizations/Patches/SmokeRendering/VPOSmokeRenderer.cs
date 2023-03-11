using UnityEngine;
using UnityEngine.Rendering;
using ValheimPerformanceOptimizations.Patches.SmokeRendering;

namespace ValheimPerformanceOptimizations.Patches
{
	public class VPOSmokeRenderer : MonoBehaviour
	{
		public static VPOSmokeRenderer Instance { get; private set; }

		private const int MaxSmokeInstances = 101;

		private static readonly int SmokeColor = Shader.PropertyToID("_Color");

		private static readonly Vector4[] SmokeColors = new Vector4[MaxSmokeInstances + 4 * 4];
		private static readonly Matrix4x4[] SmokeTransforms = new Matrix4x4[MaxSmokeInstances + 4 * 4];

		private static MaterialPropertyBlock _materialProperties;

		private static Vector4 _smokeColor;
		private static int _smokeLayer;

		private bool setupDone;

		private Material smokeMaterial;
		private Mesh smokeMesh;

		private void Awake()
		{
			Instance = this;
			_smokeLayer = LayerMask.NameToLayer("smoke");

			_materialProperties = new MaterialPropertyBlock();
		}

		private void Update()
		{
			if (!smokeMesh || !smokeMaterial)
			{
				return;
			}

			var i = 0;
			foreach (var smoke in Smoke.m_smoke)
			{
				if (smoke == null)
				{
					continue;
				}

				if (smoke.m_time > smoke.m_ttl && smoke.m_fadeTimer < 0f)
				{
					smoke.StartFadeOut();
				}

				SmokeColors[i] = _smokeColor;
				if (smoke.m_fadeTimer >= 0f)
				{
					smoke.m_fadeTimer += Time.deltaTime;
					var a = 1f - Mathf.Clamp01(smoke.m_fadeTimer / smoke.m_fadetime);

					if (smoke.m_fadeTimer >= smoke.m_fadetime)
					{
						Destroy(smoke.gameObject);
						continue;
					}

					SmokeColors[i].w = a;
				}

				var t = smoke.transform;
				SmokeTransforms[i].SetTRS(t.position, t.rotation, t.localScale);

				i += 1;
			}

			Smoke.m_smoke.RemoveAll(smoke => smoke == null);

			if (Smoke.m_smoke.Count < 1)
			{
				return;
			}

			_materialProperties.SetVectorArray(SmokeColor, SmokeColors);

			Graphics.DrawMeshInstanced(
				smokeMesh, 0, smokeMaterial,
				SmokeTransforms, i, _materialProperties,
				ShadowCastingMode.Off, false, _smokeLayer
			);
		}

		public void SetupRenderingData(GameObject smokePrefab)
		{
			if (setupDone)
			{
				return;
			}

			var meshRenderer = smokePrefab.GetComponent<MeshRenderer>();
			var meshFilter = smokePrefab.GetComponent<MeshFilter>();

			var material = meshRenderer.sharedMaterial;
			var mesh = meshFilter.sharedMesh;

			material.shader = SmokeRenderingPatch.SmokeShader;
			material.enableInstancing = true;

			material.SetFloat("_ColorMode", 0);
			material.SetFloat("_AddLightsPerPixel", 0);
			material.SetFloat("_EnableShadows", 0);
			material.SetFloat("_LightingEnabled", 0);
			material.SetFloat("_ShadowsPerPixel", 0);
			material.SetFloat("_LocalAmbientLighting", 1);
			material.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));

			material.DisableKeyword("GEOM_TYPE_BRANCH");
			material.DisableKeyword("GEOM_TYPE_BRANCH_DETAIL");
			material.DisableKeyword("GEOM_TYPE_FROND");
			material.DisableKeyword("_EMISSION");
			material.DisableKeyword("GEOM_TYPE_MESH");

			material.EnableKeyword("GEOM_TYPE_LEAF");

			smokeMaterial = material;
			smokeMesh = mesh;

			_smokeColor = material.color;

			for (var i = 0; i < MaxSmokeInstances + 4 * 4; i++)
			{
				SmokeColors[i] = _smokeColor;
			}

			setupDone = true;
		}
	}
}
