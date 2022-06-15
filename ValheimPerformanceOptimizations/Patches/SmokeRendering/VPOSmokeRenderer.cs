using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using ValheimPerformanceOptimizations.Patches.SmokeRendering;
namespace ValheimPerformanceOptimizations.Patches
{
	public class VPOSmokeRenderer : MonoBehaviour
	{
		public const int MaxSmokeInstances = 101;

		private static readonly int SmokeColor = Shader.PropertyToID("_Color");

		private static readonly Vector4[] SmokeColors = new Vector4[MaxSmokeInstances + 4 * 4];
		private static readonly Matrix4x4[] SmokeTransforms = new Matrix4x4[MaxSmokeInstances + 4 * 4];

		private static MaterialPropertyBlock _materialProperties;

		private static Vector4 _smokeColor;

		private static int _smokeLayer;

		private readonly List<SmokeGroupDrawData> freeSmokeGroups = new List<SmokeGroupDrawData>();

		private readonly Dictionary<VPOSmokeSpawner, SmokeGroupDrawData> smokeGroupsBySpawner = new Dictionary<VPOSmokeSpawner, SmokeGroupDrawData>();

		private bool setupDone;

		private Material smokeMaterial;
		private Mesh smokeMesh;

		private void Awake()
		{
			_smokeLayer = LayerMask.NameToLayer("smoke");

			VPOSmokeSpawner.SpawnerAdded += SpawnerAdded;
			VPOSmokeSpawner.SpawnerDestroyed += SpawnerRemoved;

			_materialProperties = new MaterialPropertyBlock();
		}

		private void Update()
		{
			if (!smokeMesh || !smokeMaterial)
			{
				return;
			}

			foreach (var smokeGroup in smokeGroupsBySpawner.Values)
			{
				smokeGroup.DrawInstances(smokeMesh, smokeMaterial);
			}

			freeSmokeGroups.RemoveAll(group => group.InstanceCount < 1);
			foreach (var smokeGroup in freeSmokeGroups)
			{
				smokeGroup.DrawInstances(smokeMesh, smokeMaterial);
			}
		}

		private void OnDestroy()
		{
			VPOSmokeSpawner.SpawnerAdded -= SpawnerAdded;
			VPOSmokeSpawner.SpawnerDestroyed -= SpawnerRemoved;
		}

		private void SpawnerAdded(VPOSmokeSpawner spawner)
		{
			SetupRenderingData();

			var groupData = new SmokeGroupDrawData();
			smokeGroupsBySpawner[spawner] = groupData;
			spawner.SmokeSpawned += smoke => groupData.AddInstance(smoke);
		}

		private void SpawnerRemoved(VPOSmokeSpawner spawner)
		{
			var groupData = smokeGroupsBySpawner[spawner];
			if (groupData.InstanceCount > 0)
			{
				freeSmokeGroups.Add(groupData);
			}
			smokeGroupsBySpawner.Remove(spawner);
		}

		public void SetupRenderingData()
		{
			if (setupDone) return;

			var meshRenderer = VPOSmokeSpawner.SmokePrefab.GetComponent<MeshRenderer>();
			var meshFilter = VPOSmokeSpawner.SmokePrefab.GetComponent<MeshFilter>();

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

		private class SmokeGroupDrawData
		{
			private readonly List<Smoke> instances = new List<Smoke>();
			public int InstanceCount => instances.Count;

			public void AddInstance(Smoke smoke)
			{
				instances.Add(smoke);
			}

			public void DrawInstances(Mesh smokeMesh, Material smokeMaterial)
			{
				var i = 0;
				foreach (var smoke in instances)
				{
					if (smoke == null) continue;

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

				instances.RemoveAll(smoke => smoke == null);

				if (instances.Count < 1) return;

				_materialProperties.SetVectorArray(SmokeColor, SmokeColors);

				Graphics.DrawMeshInstanced(
					smokeMesh, 0, smokeMaterial,
					SmokeTransforms, i, _materialProperties,
					ShadowCastingMode.Off, false, _smokeLayer
				);
			}
		}
	}
}
