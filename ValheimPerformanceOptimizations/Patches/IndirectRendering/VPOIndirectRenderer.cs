using System;
using IndirectRendering;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches.IndirectRendering;

[DefaultExecutionOrder(1000)]
internal sealed class VPOIndirectRenderer : MonoBehaviour
{
	internal static IndirectRenderer Renderer { get; private set; }

	private IndirectRenderer indirectRenderer;

	internal static void QueueView(in IndirectView view, Action<int> render)
	{
		if (Renderer == null)
		{
			render(-1);
			return;
		}

		Renderer.QueueView(view, render);
	}

	private void Awake()
	{
		indirectRenderer = new IndirectRenderer(
			new InstanceOverrideStore<VPOIndirectInstanceOverride>(),
			new IndirectRendererSettings
			{
				BatchCellSize = ZoneSystem.c_ZoneSize,
				MinimumScreenFraction = IndirectRendererPatches.MinimumScreenPercentage.Value * 0.01f,
				MultiDrawCasterResolver = MultiDrawCasters.TryGetCaster,
			});
		Renderer = indirectRenderer;
	}

	private void LateUpdate()
	{
		if (Game.instance == null || Game.instance.m_shuttingDown || Player.m_localPlayer == null
		    || !EnvMan.instance)
		{
			indirectRenderer.SkipFrame();
			return;
		}

		indirectRenderer.Render(Utils.GetMainCamera(), EnvMan.instance.m_dirLight, ShadowedLights.Active);
	}

	private void OnDestroy()
	{
		indirectRenderer.Dispose();
		MultiDrawCasters.Release();
		if (Renderer == indirectRenderer)
		{
			Renderer = null;
		}
	}
}
