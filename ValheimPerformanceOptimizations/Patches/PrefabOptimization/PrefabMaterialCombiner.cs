using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace ValheimPerformanceOptimizations.Patches
{
    /// <summary>
    /// Some piece prefabs can have up to 4 materials on one mesh, meaning they get
    /// rendered 4 times total. This affects most pieces with the straw material
    /// This patch merges materials into 2 groups: (alpha/non-alpha tested),
    /// making the prefab only render twice.
    /// </summary>
    public static class PrefabMaterialCombiner
    {
        private const string AlphaTestOn = "_ALPHATEST_ON";

        public static void CombinePrefabMaterials(GameObject prefab)
        {
            var wearNTear = prefab.GetComponent<WearNTear>();
            var inlineLodGroup = wearNTear.GetComponent<LODGroup>();
            if (inlineLodGroup != null)
            {
                var renderers = CollectValidRenderers(inlineLodGroup);
                CombineTexturesForObject(renderers);
            }
            else
            {
                var allRenderers = new List<Renderer>();
                var wornObj = wearNTear.m_worn;
                if (wornObj != null)
                {
                    allRenderers.AddRange(CollectValidRenderers(wornObj.GetComponent<LODGroup>()));
                }

                var newObj = wearNTear.m_new;
                if (newObj != null)
                {
                    allRenderers.AddRange(CollectValidRenderers(newObj.GetComponent<LODGroup>()));
                }

                var wetObj = wearNTear.m_wet;
                if (wetObj != null)
                {
                    allRenderers.AddRange(CollectValidRenderers(wetObj.GetComponent<LODGroup>()));
                }

                var brokenObj = wearNTear.m_new;
                if (brokenObj != null)
                {
                    allRenderers.AddRange(CollectValidRenderers(brokenObj.GetComponent<LODGroup>()));
                }

                CombineTexturesForObject(allRenderers);
            }
        }

        private static IEnumerable<Renderer> CollectValidRenderers(LODGroup inlineLodGroup)
        {
            return inlineLodGroup
                   .GetLODs()
                   .SelectMany(lod => lod.renderers)
                   .Where(r =>
                   {
                       var materials = r.sharedMaterials;
                       if (materials.Length <= 1) { return false; }
                       
                       return materials.GroupBy(mat => mat.IsKeywordEnabled(AlphaTestOn))
                                       .Any(group => group.Count() > 1);
                   });
        }

        private static void CombineTexturesForObject(IEnumerable<Renderer> renderers)
        {
            foreach (var renderer in renderers)
            {
                var meshFilter = renderer.gameObject.GetComponent<MeshFilter>();
                var mesh = meshFilter.sharedMesh;

                var allMaterials = new List<Material>();
                var uniqueMaterialMeshes = new List<Mesh>();

                var renderModeMaterialGroups = renderer.sharedMaterials
                                                       .GroupBy(mat => mat.IsKeywordEnabled(AlphaTestOn));
                
                var subMeshOffset = 0;
                foreach (var materialGroup in renderModeMaterialGroups)
                {
                    var groupMaterials = materialGroup.ToList();

                    var atlas = new Texture2D(1024, 1024) {filterMode = FilterMode.Point};
                    var albedoTextures = groupMaterials.Select(
                        m => ConvertToReadableTexture(m.mainTexture)).ToArray();
                    Rect[] uvBounds = atlas.PackTextures(albedoTextures, 2, 1024);

                    var normalAtlas = new Texture2D(1024, 1024) {filterMode = FilterMode.Point};
                    var normalTextures = groupMaterials.Select(
                            m => ConvertToReadableTexture(m.GetTexture("_BumpMap"))).ToArray();
                    normalAtlas.PackTextures(normalTextures, 2, 1024);

                    var oldMaterial = groupMaterials[0];
                    var material = new Material(oldMaterial.shader);
                    material.CopyPropertiesFromMaterial(oldMaterial);
                    material.SetTexture("_MainTex", atlas);
                    material.SetTexture("_BumpMap", normalAtlas);
                    material.mainTextureScale = Vector2.one;

                    if (oldMaterial.IsKeywordEnabled(AlphaTestOn))
                    {
                        material.renderQueue = (int) RenderQueue.AlphaTest;
                        material.EnableKeyword(AlphaTestOn);
                    }
                    else
                    {
                        material.SetOverrideTag("RenderType", "");
                        material.DisableKeyword(AlphaTestOn);
                    }

                    allMaterials.Add(material);

                    var subMeshes = new Mesh[groupMaterials.Count];
                    for (var i = 0; i < groupMaterials.Count; i++)
                    {
                        subMeshes[i] = ExtractMesh(mesh, i + subMeshOffset);

                        var remappedUVs = subMeshes[i].uv;
                        var subMeshUVBounds = uvBounds[i];

                        for (var j = 0; j < remappedUVs.Length; j++)
                        {
                            remappedUVs[j] = RemapUVToBounds(remappedUVs[j], subMeshUVBounds);
                        }

                        subMeshes[i].uv = remappedUVs;
                    }

                    subMeshOffset += groupMaterials.Count;
                    uniqueMaterialMeshes.Add(subMeshes.Length > 1 ? CombineMeshes(subMeshes, true) : subMeshes[0]);
                }

                var finalMesh = CombineMeshes(uniqueMaterialMeshes.ToArray(), false);

                meshFilter.mesh = finalMesh;
                renderer.materials = allMaterials.ToArray();
            }
        }

        private static Texture2D ConvertToReadableTexture(Texture source)
        {
            var renderTex = RenderTexture.GetTemporary(
                source.width,
                source.height,
                0,
                RenderTextureFormat.Default,
                RenderTextureReadWrite.sRGB);

            Graphics.Blit(source, renderTex);
            var previous = RenderTexture.active;
            RenderTexture.active = renderTex;
            var readableText = new Texture2D(source.width, source.height);
            readableText.ReadPixels(new Rect(0, 0, renderTex.width, renderTex.height), 0, 0);
            readableText.filterMode = FilterMode.Point;
            readableText.Apply();
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(renderTex);

            return readableText;
        }

        private static Mesh ExtractMesh(Mesh masterMesh, int subMeshIndex)
        {
            var indexMap = new Dictionary<int, int>();
            var meshIndices = masterMesh.GetIndices(subMeshIndex);
            var counter = 0;

            foreach (var i in meshIndices)
            {
                if (!indexMap.ContainsKey(i))
                {
                    indexMap.Add(i, counter);
                    counter += 1;
                }
            }

            var extractedVertices = new List<Vector3>();
            var extractedUVs = new List<Vector2>();
            var extractedNormals = new List<Vector3>();

            foreach (var pair in indexMap)
            {
                extractedVertices.Add(masterMesh.vertices[pair.Key]);
                extractedUVs.Add(masterMesh.uv[pair.Key]);
                extractedNormals.Add(masterMesh.normals[pair.Key]);
            }

            var subMeshTriangles = masterMesh.GetTriangles(subMeshIndex);
            var extractedTriangles = new int[subMeshTriangles.Length];
            for (var i = 0; i < subMeshTriangles.Length; i++)
            {
                extractedTriangles[i] = indexMap[subMeshTriangles[i]];
            }

            return new Mesh
            {
                vertices = extractedVertices.ToArray(),
                uv = extractedUVs.ToArray(),
                normals = extractedNormals.ToArray(),
                triangles = extractedTriangles
            };
        }

        private static Vector2 RemapUVToBounds(Vector2 oldUV, Rect bounds)
        {
            oldUV.x = Mathf.Lerp(bounds.xMin, bounds.xMax, oldUV.x);
            oldUV.y = Mathf.Lerp(bounds.yMin, bounds.yMax, oldUV.y);

            return oldUV;
        }

        private static Mesh CombineMeshes(Mesh[] meshes, bool mergeSubMeshes)
        {
            var combinedInstances = new CombineInstance[meshes.Length];
            for (var i = 0; i < combinedInstances.Length; i++)
            {
                combinedInstances[i].mesh = meshes[i];
            }

            var combinedMesh = new Mesh();
            combinedMesh.CombineMeshes(combinedInstances, mergeSubMeshes, false);

            return combinedMesh;
        }
    }
}