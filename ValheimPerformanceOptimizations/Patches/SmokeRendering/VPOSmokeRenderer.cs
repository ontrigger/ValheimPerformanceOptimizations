using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ValheimPerformanceOptimizations.Patches
{
    public class VPOSmokeRenderer : MonoBehaviour
    {
        public const int MaxSmokeInstances = 101;

        private static readonly int SmokeColor = Shader.PropertyToID("_Color");

        private Material smokeMaterial;
        private Mesh smokeMesh;

        private bool setupDone;

        private static Color _smokeColor;

        private static int _smokeLayer;

        private void Awake()
        {
            _smokeLayer = LayerMask.NameToLayer("smoke");
            
            VPOSmokeSpawner.SpawnerAdded += SpawnerCountChanged;
            VPOSmokeSpawner.SpawnerDestroyed += SpawnerCountChanged;
        }
        
        private void SpawnerCountChanged(VPOSmokeSpawner spawner)
        {
            if (!setupDone)
            {
                var meshRenderer = VPOSmokeSpawner.SmokePrefab.GetComponent<MeshRenderer>();
                var meshFilter = VPOSmokeSpawner.SmokePrefab.GetComponent<MeshFilter>();

                SetupRenderingData(meshRenderer.sharedMaterial, meshFilter.sharedMesh);
            }
        }

        private void Update()
        {
            if (!smokeMesh || !smokeMaterial)
            {
                return;
            }

            var allCombinedSpawners = CombineSmokeBySpawners();

            foreach (var combinedSpawner in allCombinedSpawners)
            {
                combinedSpawner.DrawSmoke(smokeMesh, smokeMaterial);
            }
        }

        private static List<CombinedSmokeInstances> CombineSmokeBySpawners()
        {
            var allCombinedSpawners = new List<CombinedSmokeInstances>();
            var alreadyCombined = new HashSet<int>();
            foreach (var spawner in VPOSmokeSpawner.AllSmokeSpawners)
            {
                if (alreadyCombined.Contains(spawner.GetInstanceID())) continue;

                var combined = new CombinedSmokeInstances(spawner.SmokeInstances, spawner.transform.position);
                foreach (var otherSpawner in VPOSmokeSpawner.AllSmokeSpawners)
                {
                    if (spawner == otherSpawner || alreadyCombined.Contains(otherSpawner.GetInstanceID())) continue;

                    if (combined.CombineWith(otherSpawner.SmokeInstances, otherSpawner.transform.position))
                    {
                        alreadyCombined.Add(otherSpawner.GetInstanceID());
                    }
                }

                if (combined.SmokeInstances.Count > 1)
                {
                    alreadyCombined.Add(spawner.GetInstanceID());
                }

                allCombinedSpawners.Add(combined);
            }

            if (VPOSmokeSpawner.FreeSmoke.Count > 0)
            {
                allCombinedSpawners.Add(new CombinedSmokeInstances(VPOSmokeSpawner.FreeSmoke, Vector3.zero));
            }

            return allCombinedSpawners;
        }

        public void SetupRenderingData(Material material, Mesh mesh)
        {
            if (setupDone) return;

            material.shader = SmokeRenderingPatch.SmokeShader;
            material.enableInstancing = true;

            material.SetFloat("_ColorMode", 0);
            material.SetFloat("_AddLightsPerPixel", 0);
            material.SetFloat("_EnableShadows", 0);
            material.SetFloat("_LightingEnabled", 0);
            material.SetFloat("_ShadowsPerPixel", 0);
            material.SetFloat("_LocalAmbientLighting", 1);
            material.SetColor("_TintColor", new Color(0.7f, 0.7f, 0.7f, 1f));
            
            
            material.DisableKeyword("GEOM_TYPE_BRANCH");
            material.DisableKeyword("GEOM_TYPE_FROND");
            material.DisableKeyword("GEOM_TYPE_MESH");
            material.EnableKeyword("GEOM_TYPE_LEAF");
            material.EnableKeyword("GEOM_TYPE_BRANCH_DETAIL");

            /*var translucency = smokeMaterial.GetFloat(TranslucencyID);
                smokeMaterial.SetFloat(TranslucencyID, translucency);*/

            smokeMaterial = material;
            smokeMesh = mesh;

            _smokeColor = material.color;

            setupDone = true;
        }

        private class CombinedSmokeInstances
        {
            public readonly List<List<Smoke>> SmokeInstances = new List<List<Smoke>>();
            
            private Vector3 center;

            private float radius = 8 * 8;

            private readonly Vector4[] smokeColors = new Vector4[MaxSmokeInstances + 4 * 4];
            private readonly Matrix4x4[] smokeTransforms = new Matrix4x4[MaxSmokeInstances + 4 * 4];

            private readonly MaterialPropertyBlock materialProperties = new MaterialPropertyBlock();

            public CombinedSmokeInstances(List<Smoke> instances, Vector3 instanceSpawnPosition)
            {
                SmokeInstances.Add(instances);
                center = instanceSpawnPosition;
            }

            public bool CombineWith(List<Smoke> instances, Vector3 instanceSpawnPosition)
            {
                var vec1 = center - instanceSpawnPosition;
                var sqrDistance = Vector3.SqrMagnitude(vec1);

                if (sqrDistance < radius && SmokeInstances.Count < 6)
                {
                    SmokeInstances.Add(instances);

                    radius = (radius * 2 + sqrDistance) / 2f;
                    center = (center + instanceSpawnPosition) / 2f;

                    return true;
                }

                return false;
            }

            public void DrawSmoke(Mesh smokeMesh, Material smokeMaterial)
            {
                var i = 0;
                foreach (var instances in SmokeInstances)
                {
                    instances.RemoveAll(smoke => smoke == null);

                    instances.ForEach(smoke =>
                    {
                        if (smoke.m_time > smoke.m_ttl && smoke.m_fadeTimer < 0f)
                        {
                            smoke.StartFadeOut();
                        }

                        smokeColors[i] = _smokeColor;
                        if (smoke.m_fadeTimer >= 0f)
                        {
                            smoke.m_fadeTimer += Time.deltaTime;
                            var a = 1f - Mathf.Clamp01(smoke.m_fadeTimer / smoke.m_fadetime);
                            smokeColors[i].w = a;

                            if (smoke.m_fadeTimer >= smoke.m_fadetime)
                            {
                                Smoke.m_smoke.Remove(smoke);
                                Destroy(smoke.gameObject);
                                return;
                            }
                        }

                        var t = smoke.transform;
                        smokeTransforms[i].SetTRS(t.position, t.rotation, t.localScale);

                        i += 1;
                    });
                }

                materialProperties.SetVectorArray(SmokeColor, smokeColors);

                Graphics.DrawMeshInstanced(
                    smokeMesh, 0, smokeMaterial,
                    smokeTransforms, i, materialProperties,
                    ShadowCastingMode.Off, false, _smokeLayer
                );
            }
        }
    }
}