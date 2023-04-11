using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches.OcclusionCulling
{
	public class VPORendererTracker : MonoBehaviour
	{
		public int ID { get; set; }
		
		public Renderer renderer;

		private void OnEnable()
		{
			if (renderer != null)
			{
				ID = VPOOcclusionRenderer.Instance.AddInstance(this);
			}
		}

		private void OnDisable()
		{
			if (ID != -1)
			{
				VPOOcclusionRenderer.Instance.RemoveInstance(this);
				ID = -1;
			}
		}
	}
}
