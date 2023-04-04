using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Rendering;
public class DownSampleDepth : MonoBehaviour
{
	public Shader downSampleShader;

	private Material material;

	private RenderTexture hiZDepthTexture;
	private CommandBuffer commandBuffer;

	private int[] tempMipIds;

	private void Awake()
	{
		material = new Material(downSampleShader);
	}

	public void DownSample(RenderTexture src, RenderTexture dst)
	{
		Assert.IsTrue(dst.width == dst.height, "dst.width != dst.height");

		var lastActive = RenderTexture.active;
		
		var size = dst.width;
		var mipCount = (int)Mathf.Floor(Mathf.Log(size, 2f));
		
		Graphics.Blit(src, dst);

		RenderTexture lastRt = null;
		for (var i = 0; i < mipCount; ++i)
		{
			size >>= 1;
			size = Mathf.Max(size, 1);

			var tempRT = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.RHalf, RenderTextureReadWrite.Linear);
			tempRT.filterMode = FilterMode.Point;

			if (lastRt == null)
			{
				material.SetTexture("_MainTex", lastRt);
				Graphics.Blit(dst, tempRT, material, 0);
			}
			else
			{
				material.SetTexture("_MainTex", lastRt);
				Graphics.Blit(lastRt, tempRT, material, 0);
				RenderTexture.ReleaseTemporary(lastRt);
			}

			lastRt = tempRT;

			// no way to write straight to mips so we copy instead
			Graphics.CopyTexture(tempRT, 0, 0, dst, 0, i + 1);
		}
		
		RenderTexture.ReleaseTemporary(lastRt);

		RenderTexture.active = lastActive;
	}
}
