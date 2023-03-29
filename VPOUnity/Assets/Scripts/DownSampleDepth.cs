using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Rendering;
public class DownSampleDepth : MonoBehaviour
{
	public Shader downSampleShader;
	public CameraEvent cameraEvent;

	[HideInInspector]
	public Vector2 TextureSize;
	
	public RenderTexture Texture => hiZDepthTexture;
	
	new private Camera camera;
	private Material material;

	private RenderTexture hiZDepthTexture;
	private CommandBuffer commandBuffer;

	private int[] tempMipIds;

	public void Initialize()
	{
		//ReallocateBuffers();
	}

	private void Awake()
	{
		camera = GetComponent<Camera>();
		material = new Material(downSampleShader);
	}

	private void OnDisable()
	{
		if (camera != null)
		{
			if (commandBuffer != null)
			{
				camera.RemoveCommandBuffer(cameraEvent, commandBuffer);
				commandBuffer = null;
			}
		}
        
		if (hiZDepthTexture != null)
		{
			hiZDepthTexture.Release();
			hiZDepthTexture = null;
		}
	}
	
	private void OnPreRender()
	{
		//ReallocateBuffers();
	}

	private void ReallocateBuffers()
	{
		var size = Mathf.Max(camera.pixelWidth, camera.pixelHeight);
		size = Mathf.Min(Mathf.NextPowerOfTwo(size), 2048);

		TextureSize = new Vector2(size, size);

		var isCommandBufferInvalid = commandBuffer == null;
		if (hiZDepthTexture == null || hiZDepthTexture.width != size || hiZDepthTexture.height != size)
		{
			if (hiZDepthTexture != null)
			{
				hiZDepthTexture.Release();
			}

			hiZDepthTexture = new RenderTexture(size, size, 0, RenderTextureFormat.RHalf, RenderTextureReadWrite.Linear);
			hiZDepthTexture.filterMode = FilterMode.Point;
			hiZDepthTexture.useMipMap = true;
			hiZDepthTexture.autoGenerateMips = false;
			hiZDepthTexture.Create();
			hiZDepthTexture.hideFlags = HideFlags.HideAndDontSave;

			isCommandBufferInvalid = true;
		}

		if (isCommandBufferInvalid)
		{
			var mipCount = (int)Mathf.Floor(Mathf.Log(size, 2f));
			tempMipIds = new int[mipCount];

			if (commandBuffer != null)
			{
				camera.RemoveCommandBuffer(cameraEvent, commandBuffer);
			}

			commandBuffer = new CommandBuffer();
			commandBuffer.name = "Hi-Z Buffer";


			var id = new RenderTargetIdentifier(hiZDepthTexture);
			commandBuffer.Blit(null, id, material, 1);

			for (var i = 0; i < mipCount; ++i)
			{
				tempMipIds[i] = Shader.PropertyToID("_09659d57_Temporaries" + i);
				size >>= 1;
				size = Mathf.Max(size, 1);

				commandBuffer.GetTemporaryRT(tempMipIds[i], size, size, 0, FilterMode.Point, RenderTextureFormat.RHalf, RenderTextureReadWrite.Linear);

				if (i == 0)
				{
					commandBuffer.Blit(id, tempMipIds[0], material);
				}
				else
				{
					commandBuffer.Blit(tempMipIds[i - 1], tempMipIds[i], material);
				}

				// no way to write straight to mips so we copy instead
				commandBuffer.CopyTexture(tempMipIds[i], 0, 0, id, 0, i + 1);

				if (i >= 1)
				{
					commandBuffer.ReleaseTemporaryRT(tempMipIds[i - 1]);
				}
			}

			commandBuffer.ReleaseTemporaryRT(tempMipIds[mipCount - 1]);
			camera.AddCommandBuffer(cameraEvent, commandBuffer);
		}
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