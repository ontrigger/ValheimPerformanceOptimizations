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

		var size = dst.width;
		var mipCount = (int)Mathf.Floor(Mathf.Log(size, 2f));
		var tempMips = new RenderTexture[mipCount];
		
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
				Graphics.Blit(dst, tempMips[0], material);
			}
			else
			{
				Graphics.Blit(tempMips[i - 1], tempMips[i], material);
			}

			// no way to write straight to mips so we copy instead
			Graphics.CopyTexture(tempMips[i], 0, 0, dst, 0, i + 1);

			if (i >= 1)
			{
				RenderTexture.ReleaseTemporary(tempMips[i - 1]);
			}
		}

		RenderTexture.ReleaseTemporary(tempMips[mipCount - 1]);
	}
}
