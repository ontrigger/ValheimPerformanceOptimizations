using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches
{
    /// <summary>
    ///     The Lux shader does not force quads to look at the camera,
    ///     so we do it ourselves
    /// </summary>
    public class VPOSmokeRenderer : MonoBehaviour
    {
        private void Update()
        {
            var camera = Utils.GetMainCamera();
            if (!camera) return;

            var camPosition = camera.transform.position;

            foreach (var smoke in Smoke.m_smoke)
            {
                smoke.transform.rotation = Quaternion.LookRotation(smoke.transform.position - camPosition);
            }
        }
    }
}