using System.Runtime.CompilerServices;
using RuntimeDebugDraw;
using UnityEngine;
using UnityEngine.Rendering;

namespace ValheimPerformanceOptimizations
{
    public class SmokePatches
    {
        private static readonly ConditionalWeakTable<Smoke, IntWrapper> Data =
            new ConditionalWeakTable<Smoke, IntWrapper>();

        private static void DrawBounds(Bounds b, float delay = 0)
        {
            // bottom
            var p1 = new Vector3(b.min.x, b.min.y, b.min.z);
            var p2 = new Vector3(b.max.x, b.min.y, b.min.z);
            var p3 = new Vector3(b.max.x, b.min.y, b.max.z);
            var p4 = new Vector3(b.min.x, b.min.y, b.max.z);

            Draw.DrawLine(p1, p2, Color.blue, delay);
            Draw.DrawLine(p2, p3, Color.red, delay);
            Draw.DrawLine(p3, p4, Color.yellow, delay);
            Draw.DrawLine(p4, p1, Color.magenta, delay);

            // top
            var p5 = new Vector3(b.min.x, b.max.y, b.min.z);
            var p6 = new Vector3(b.max.x, b.max.y, b.min.z);
            var p7 = new Vector3(b.max.x, b.max.y, b.max.z);
            var p8 = new Vector3(b.min.x, b.max.y, b.max.z);

            Draw.DrawLine(p5, p6, Color.blue, delay);
            Draw.DrawLine(p6, p7, Color.red, delay);
            Draw.DrawLine(p7, p8, Color.yellow, delay);
            Draw.DrawLine(p8, p5, Color.magenta, delay);

            // sides
            Draw.DrawLine(p1, p5, Color.white, delay);
            Draw.DrawLine(p2, p6, Color.gray, delay);
            Draw.DrawLine(p3, p7, Color.green, delay);
            Draw.DrawLine(p4, p8, Color.cyan, delay);
        }

        /*[HarmonyPatch(typeof(Smoke), "Awake")]
        public static class Smoke_Awake_Patch
        {
            private static bool Prefix(Smoke __instance)
            {
                __instance.m_body = __instance.GetComponent<Rigidbody>();
                
                var intWrapper = Data.GetOrCreateValue(__instance);
                intWrapper.Index = Smoke.m_smoke.Count;
                
                Smoke.m_smoke.Add(__instance);
                __instance.m_added = true;
                __instance.m_mr = __instance.GetComponent<MeshRenderer>();
                __instance.m_vel += Quaternion.Euler(0f, Random.Range(0, 360), 0f) * Vector3.forward * __instance.m_randomVel;

                var meshFilter = __instance.GetComponent<MeshFilter>();
                var meshRenderer = __instance.m_mr;

                VPOSmokeRenderer.SetSmokeData(meshRenderer.sharedMaterial, meshFilter.sharedMesh);
                /*VPOSmokeRenderer.SetInstanceAlpha(intWrapper.Index, 1.0f);#1#
                Object.Destroy(__instance.m_mr);

                return false;
            }
        }

        [HarmonyPatch(typeof(Smoke), "Update")]
        public static class Smoke_Update_Patch
        {
            private static bool Prefix(Smoke __instance)
            {
                __instance.m_time += Time.deltaTime;
                if (__instance.m_time > __instance.m_ttl && __instance.m_fadeTimer < 0f)
                {
                    __instance.StartFadeOut();
                }

                float num = 1f - Mathf.Clamp01(__instance.m_time / __instance.m_ttl);
                __instance.m_body.mass = num * num;
                Vector3 velocity = __instance.m_body.velocity;
                Vector3 vel = __instance.m_vel;
                vel.y *= num;
                Vector3 vector = vel - velocity;
                __instance.m_body.AddForce(vector * __instance.m_force * Time.deltaTime, ForceMode.VelocityChange);
                if (__instance.m_fadeTimer >= 0f)
                {
                    __instance.m_fadeTimer += Time.deltaTime;
                    /*float a = 1f - Mathf.Clamp01(__instance.m_fadeTimer / __instance.m_fadetime);

                    var myIndex = Data.GetOrCreateValue(__instance);
                    VPOSmokeRenderer.SetInstanceAlpha(myIndex.Index, a);
                    Color color = __instance.m_mr.material.color;
                    color.a = a;
                    __instance.m_mr.material.color = color;#1#
                    if (__instance.m_fadeTimer >= __instance.m_fadetime)
                    {
                        Object.Destroy(__instance.gameObject);
                    }
                }

                return false;
            }
        }*/

        private class IntWrapper
        {
            public int Index { get; set; }
        }
    }

    public class VPOSmokeRenderer : MonoBehaviour
    {
        public static VPOSmokeRenderer Instance;

        private static readonly int SmokeColor = Shader.PropertyToID("_Color");

        private static int _smokeLayer;
        private static readonly int AddLightsPerPixel = Shader.PropertyToID("_AddLightsPerPixel");
        private static readonly int LightingEnabled = Shader.PropertyToID("_LightingEnabled");
        private static readonly int SrcBlend = Shader.PropertyToID("_SrcBlend");
        private static readonly int DstBlend = Shader.PropertyToID("_DstBlend");
        private static readonly int ZWrite = Shader.PropertyToID("_ZWrite");
        private readonly Vector4[] smokeColors = new Vector4[101];

        private readonly Matrix4x4[] smokeTransforms = new Matrix4x4[101];

        private MaterialPropertyBlock materialProperties;
        private Material smokeMaterial;
        private Mesh smokeMesh;

        private void Awake()
        {
            Instance = this;

            for (var i = 0; i < smokeTransforms.Length; i++)
            {
                smokeTransforms[i] = Matrix4x4.identity;
            }

            materialProperties = new MaterialPropertyBlock();
            _smokeLayer = LayerMask.NameToLayer("smoke");
        }

        private void Update()
        {
            if (!smokeMesh || !smokeMaterial)
            {
                return;
            }

            var j = -1;
            for (var i = 0; i < Smoke.m_smoke.Count; i++)
            {
                var smoke = Smoke.m_smoke[i];

                var t = smoke.transform;
                smokeTransforms[i].SetTRS(t.position, t.rotation, t.lossyScale);

                /*if (smoke.m_fadeTimer >= 0f)
                {
                    var alpha = 1f - Mathf.Clamp01(smoke.m_fadeTimer / smoke.m_fadetime);

                    var color = smokeColors[i];
                    color.w = alpha;
                    smokeColors[i] = color;
                }*/
            }

            materialProperties.SetVectorArray(SmokeColor, smokeColors);

            Graphics.DrawMeshInstanced(
                smokeMesh, 0, smokeMaterial,
                smokeTransforms, Smoke.m_smoke.Count, materialProperties,
                ShadowCastingMode.Off, false, _smokeLayer
            );
        }

        public static void SetSmokeData(Material mat, Mesh mesh)
        {
            if (!Instance.smokeMaterial)
            {
                Instance.smokeMaterial = mat;

                Vector4 smokeColor = Instance.smokeMaterial.color;
                for (var i = 0; i < Instance.smokeColors.Length; i++)
                {
                    Instance.smokeColors[i] = smokeColor;
                }
            }

            if (!Instance.smokeMesh)
            {
                Instance.smokeMesh = mesh;
            }
        }

        public static void SetInstanceAlpha(int instance, float alpha)
        {
            var color = Instance.smokeColors[instance];
            color.w = alpha;
            Instance.smokeColors[instance] = color;
        }
    }
}