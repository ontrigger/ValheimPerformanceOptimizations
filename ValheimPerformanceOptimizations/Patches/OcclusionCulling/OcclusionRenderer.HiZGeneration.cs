using UnityEngine;
using UnityEngine.Assertions;

namespace ValheimPerformanceOptimizations.Patches.OcclusionCulling
{
	public partial class VPOOcclusionRenderer
	{
		private RenderTexture[] tempMips;
		private int lastMipCount;
		
		private static readonly int MainTexId = Shader.PropertyToID("_MainTex");

		public void DownsampleDepth(RenderTexture src, RenderTexture dst)
		{
			Assert.IsTrue(dst.width == dst.height, "dst.width != dst.height");

			var lastActive = RenderTexture.active;

			var size = dst.width;
			var mipCount = (int)Mathf.Floor(Mathf.Log(size, 2f));
			if (mipCount != lastMipCount)
			{
				tempMips = new RenderTexture[mipCount];
				lastMipCount = mipCount;
			}
		
			Graphics.Blit(src, dst);

			for (var i = 0; i < mipCount; ++i)
			{
				size >>= 1;
				size = Mathf.Max(size, 1);

				var tempRT = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.RHalf, RenderTextureReadWrite.Linear);
				tempRT.filterMode = FilterMode.Point;

				tempMips[i] = tempRT;

				if (i == 0)
				{
					_downsampleMaterial.SetTexture(MainTexId, tempMips[0]);
					Graphics.Blit(dst, tempMips[0], _downsampleMaterial, 0);
				}
				else
				{
					_downsampleMaterial.SetTexture(MainTexId, tempMips[i - 1]);
					Graphics.Blit(tempMips[i - 1], tempMips[i], _downsampleMaterial, 0);
				}

				// no way to write straight to mips so we copy instead
				Graphics.CopyTexture(tempMips[i], 0, 0, dst, 0, i + 1);

				if (i >= 1)
				{
					RenderTexture.ReleaseTemporary(tempMips[i - 1]);
				}
			}

			RenderTexture.ReleaseTemporary(tempMips[mipCount - 1]);

			RenderTexture.active = lastActive;
		}
	}
}
