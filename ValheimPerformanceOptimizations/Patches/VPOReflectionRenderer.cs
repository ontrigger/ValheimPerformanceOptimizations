using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace ValheimPerformanceOptimizations.Patches;

/// <summary>
/// renders reflection probes one face at a time
/// </summary>
[HarmonyPatch]
public class VPOReflectionRenderer : ReflectionUpdate
{
	public const int CubemapSize = 128;

	public float farClip = 1000f;
	public float lodBias = 5f;

	private Camera cam;
	private RenderTexture cubemap1;
	private RenderTexture cubemap2;

	private CubemapFace nextFace = CubemapFace.Unknown; // -1 = idle, 0..5 = rendering faces
	private bool isFinished;
	private Vector3 renderPosition;

	private LayerMask characterMask;
	private LayerMask effectMask;
	private LayerMask itemMask;
	private LayerMask pieceMask;
	private LayerMask transparentFXMask;
	
	private static readonly float[] LayerCullDistances = new float[32];

	[HarmonyPatch(typeof(ReflectionUpdate), nameof(Start))]
	private static bool Prefix(ReflectionUpdate __instance)
	{
		var newEffectArea = __instance.gameObject.AddComponent<VPOReflectionRenderer>();

		newEffectArea.m_probe1 = __instance.m_probe1;
		newEffectArea.m_probe2 = __instance.m_probe2;

		newEffectArea.m_interval = __instance.m_interval;
		newEffectArea.m_reflectionHeight = __instance.m_reflectionHeight;
		newEffectArea.m_transitionDuration = __instance.m_transitionDuration;
		newEffectArea.m_power = __instance.m_power;

		__instance.enabled = false;
		//Destroy(__instance);

		return false;
	}

	[HarmonyPatch(typeof(ReflectionUpdate), nameof(ReflectionUpdate.UpdateReflection))]
	[HarmonyPrefix]
	private static bool UpdateReflection_Prefix(ReflectionUpdate __instance)
	{
		if (__instance is not VPOReflectionRenderer renderer)
		{
			return true;
		}

		renderer.RequestCubemapRender();
		return false;
	}

	new private void Start()
	{
		cam = gameObject.AddComponent<Camera>();
		cam.enabled = false;
		cam.farClipPlane = farClip;

		cubemap1 = CreateCubemapRT();
		cubemap2 = CreateCubemapRT();
		
		characterMask = LayerMask.NameToLayer("character");
		effectMask = LayerMask.NameToLayer("effect");
		itemMask = LayerMask.NameToLayer("item");
		pieceMask = LayerMask.NameToLayer("piece");
		transparentFXMask = LayerMask.NameToLayer("TransparentFX");

		m_instance = this;
		m_current = m_probe1;
	}

	private static RenderTexture CreateCubemapRT()
	{
		var rt = new RenderTexture(CubemapSize, CubemapSize, 16)
		{
			dimension = TextureDimension.Cube, useMipMap = true, autoGenerateMips = true,
		};
		return rt;
	}

	new private void OnDestroy()
	{
		if (cubemap1)
		{
			cubemap1.Release();
		}
		if (cubemap2)
		{
			cubemap2.Release();
		}

		m_instance = null;
	}

	new private void Update()
	{
		m_updateTimer += Time.deltaTime;

		if (nextFace == CubemapFace.Unknown && m_updateTimer >= m_interval)
		{
			m_updateTimer = 0f;
			StartCubemapRender();
		}

		if (nextFace is >= 0 and <= CubemapFace.NegativeZ)
		{
			RenderFace(nextFace);
			nextFace += 1;
			if (nextFace > CubemapFace.NegativeZ)
			{
				EndCubemapRender();
			}
		}

		if (isFinished)
		{
			var f = Mathf.Clamp01(m_updateTimer / m_transitionDuration);
			f = Mathf.Pow(f, m_power);

			if (m_current == m_probe1)
			{
				m_probe1.importance = 1;
				m_probe2.importance = 0;
				m_probe1.size = new Vector3(2000f * f, 1000f * f, 2000f * f);
				m_probe2.size = new Vector3(2001f, 1001f, 2001f);
			}
			else
			{
				m_probe1.importance = 0;
				m_probe2.importance = 1;
				m_probe2.size = new Vector3(2000f * f, 1000f * f, 2000f * f);
				m_probe1.size = new Vector3(2001f, 1001f, 2001f);
			}
		}
	}

	private void StartCubemapRender()
	{
		if (nextFace == CubemapFace.Unknown)
		{
			m_current = m_current == m_probe1 ? m_probe2 : m_probe1;
		}

		renderPosition = ZNet.instance.GetReferencePosition();
		renderPosition += Vector3.up * m_reflectionHeight;
		m_current.transform.position = renderPosition;

		nextFace = CubemapFace.PositiveX;
		isFinished = false;
	}

	private void RequestCubemapRender()
	{
		m_updateTimer = 0f;
		StartCubemapRender();
	}

	private void RenderFace(CubemapFace face)
	{
		cam.transform.position = renderPosition;

		var target = m_current == m_probe1 ? cubemap1 : cubemap2;

		var oldLodBias = QualitySettings.lodBias;
		var oldCascades = QualitySettings.shadowCascades;
		var oldShadowDistance = QualitySettings.shadowDistance;
		var oldMaximumLodLevel = QualitySettings.maximumLODLevel;

		try
		{
			QualitySettings.lodBias = lodBias;
			QualitySettings.shadowCascades = 2;
			QualitySettings.shadowDistance = 80;
			QualitySettings.maximumLODLevel = 1; // 2 removes most objects unfortunately

			cam.farClipPlane = farClip;
			var excludeMask = (1 << characterMask) | (1 << effectMask) | (1 << itemMask) | (1 << transparentFXMask);
			cam.cullingMask = m_probe1.cullingMask & ~excludeMask;
			/*for (var i = 0; i < 32; i++)
			{
				LayerCullDistances[i] = ((1 << i) & cam.cullingMask) != 0 ? 1000f : 0f;
			}*/
			LayerCullDistances[pieceMask.value] = 500f; // half distance for pieces
			cam.layerCullDistances = LayerCullDistances;
			cam.RenderToCubemap(target, 1 << (int)face);
		}
		finally
		{
			QualitySettings.lodBias = oldLodBias;
			QualitySettings.shadowCascades = oldCascades;
			QualitySettings.shadowDistance = oldShadowDistance;
			QualitySettings.maximumLODLevel = oldMaximumLodLevel;
		}
	}

	private void EndCubemapRender()
	{
		nextFace = CubemapFace.Unknown;
		isFinished = true;

		var target = m_current == m_probe1 ? cubemap1 : cubemap2;
		m_current.realtimeTexture = target;
	}
}
