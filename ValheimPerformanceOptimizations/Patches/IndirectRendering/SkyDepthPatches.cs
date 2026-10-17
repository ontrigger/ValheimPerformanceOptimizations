using HarmonyLib;
using IndirectRendering;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches.IndirectRendering;

/// <summary>
/// DepthCamera renders the distance to the nearest surface above the player.
/// Its replacement shader cannot read the indirect instance buffers, so the
/// render waits for IndirectRenderer to cull its view and our draws follow it.
/// </summary>
[HarmonyPatch]
internal static class SkyDepthPatches
{
	private static readonly int SkyAlphaTextureId = Shader.PropertyToID("_SkyAlphaTexture");
	private static readonly int SkyAlphaPositionId = Shader.PropertyToID("_SkyAlphaPosition");

	[HarmonyPatch(typeof(DepthCamera), nameof(DepthCamera.RenderDepth))]
	[HarmonyPrefix]
	private static bool RenderDepthPrefix(DepthCamera __instance)
	{
		if (VPOIndirectRenderer.Renderer == null)
		{
			return true;
		}

		var mainCamera = Utils.GetMainCamera();

		var position = (Player.m_localPlayer ? Player.m_localPlayer.transform.position : mainCamera.transform.position)
			+ Vector3.up * __instance.m_offset;
		position.x = Mathf.Round(position.x);
		position.y = Mathf.Round(position.y);
		position.z = Mathf.Round(position.z);
		__instance.transform.position = position;

		VPOIndirectRenderer.QueueView(
			IndirectView.FromCamera(__instance.m_camera, IndirectView.Lod0Only),
			viewIndex => Render(__instance, viewIndex));
		return false;
	}

	private static void Render(DepthCamera depthCamera, int viewIndex)
	{
		if (!depthCamera)
		{
			return;
		}

		var camera = depthCamera.m_camera;
		var lodBias = QualitySettings.lodBias;
		QualitySettings.lodBias = 10f;
		camera.RenderWithShader(depthCamera.m_depthShader, "RenderType");
		QualitySettings.lodBias = lodBias;

		VPOIndirectRenderer.Renderer?.RenderViewWithShader(viewIndex, camera, PieceShaderBundle.SkyDepthShader);

		Shader.SetGlobalTexture(SkyAlphaTextureId, depthCamera.m_texture);
		Shader.SetGlobalVector(SkyAlphaPositionId, depthCamera.transform.position);
	}
}
