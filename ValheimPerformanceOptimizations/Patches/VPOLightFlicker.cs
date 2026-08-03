using System;
using HarmonyLib;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using VPO = ValheimPerformanceOptimizations.ValheimPerformanceOptimizations;

namespace ValheimPerformanceOptimizations.Patches;

/// <summary>
/// this is barely faster than orig, might remove
/// </summary>
[HarmonyPatch]
public sealed class VPOLightFlicker : LightFlicker, IMonoUpdater
{
	private static Camera _cachedCamera;

	private static CullingGroup _cullingGroup;
	private static BoundingSphere[] _boundingSpheres = new BoundingSphere[64];
	private static VPOLightFlicker[] _cullingEntries = new VPOLightFlicker[64];
	private static int _boundingSphereCount;
	private static readonly float[] UpdateDistances = { 50f };

	private static readonly ProfilerMarker UpdateBoundingSphereMarker = new("VPOLightFlicker.UpdateBoundingSphere");

	private Transform parentTransform;
	private bool parentHasZSyncTransform;
	private int boundingSphereIndex = -1;

	new private void Awake()
	{
		base.Awake();

		parentTransform = transform.parent;
		parentHasZSyncTransform = parentTransform && parentTransform.GetComponent<ZSyncTransform>();
	}

	new private void OnEnable()
	{
		base.OnEnable();
		RegisterBoundingSphere();
	}

	new private void OnDisable()
	{
		base.OnDisable();
		UnregisterBoundingSphere();
	}

	new private void CustomUpdate(float deltaTime, float time)
	{
		Profiler.BeginSample("VPOLightFlicker.CustomUpdate");
		if (IsVisible())
		{
			Profiler.BeginSample("VPOLightFlicker.CustomUpdate.Base");
			base.CustomUpdate(deltaTime, time);
			Profiler.EndSample();
			Profiler.EndSample();
			return;
		}

		Profiler.BeginSample("VPOLightFlicker.CustomUpdate.UpdateLifetime");
		UpdateLifetime(deltaTime);
		Profiler.EndSample();
		Profiler.EndSample();
	}

	void IMonoUpdater.CustomUpdate(float deltaTime, float time)
	{
		CustomUpdate(deltaTime, time);
	}

	private bool IsVisible()
	{
		if (!m_light || boundingSphereIndex < 0) { return true; }

		UpdateBoundingSphereIfParentMoved();
		return _cullingGroup.IsVisible(boundingSphereIndex);
	}

	private void UpdateLifetime(float deltaTime)
	{
		if (!m_light)
		{
			ZLog.LogError("Light was null! This should never happen!");
			return;
		}

		if (!m_light.enabled) { return; }

		m_time += deltaTime;
		if (m_ttl > 0f && m_time > m_ttl)
		{
			Destroy(gameObject);
		}
	}

	private void UpdateBoundingSphereIfParentMoved()
	{
		if (!parentTransform) { return; }

		var parentChanged = parentTransform.hasChanged;
		if (!parentHasZSyncTransform && !parentChanged) { return; }

		_boundingSpheres[boundingSphereIndex].position = transform.position;
		if (parentChanged)
		{
			parentTransform.hasChanged = false;
		}
	}

	private void RegisterBoundingSphere()
	{
		if (_cullingGroup == null) { return; }

		if (_boundingSphereCount == _boundingSpheres.Length)
		{
			var newCapacity = _boundingSpheres.Length * 2;
			Array.Resize(ref _boundingSpheres, newCapacity);
			Array.Resize(ref _cullingEntries, newCapacity);
			_cullingGroup.SetBoundingSpheres(_boundingSpheres);
		}

		boundingSphereIndex = _boundingSphereCount;
		_boundingSphereCount += 1;
		_boundingSpheres[boundingSphereIndex] = new BoundingSphere(
			transform.position, m_light.range * 1.2f
		);
		_cullingEntries[boundingSphereIndex] = this;
		_cullingGroup.SetBoundingSphereCount(_boundingSphereCount);
	}

	private void UnregisterBoundingSphere()
	{
		if (boundingSphereIndex < 0 || _cullingGroup == null) { return; }

		var index = boundingSphereIndex;
		var entryCount = _boundingSphereCount;

		_cullingGroup.EraseSwapBack(index);
		CullingGroup.EraseSwapBack(index, _boundingSpheres, ref _boundingSphereCount);
		CullingGroup.EraseSwapBack(index, _cullingEntries, ref entryCount);
		if (index < _boundingSphereCount)
		{
			_cullingEntries[index].boundingSphereIndex = index;
		}

		boundingSphereIndex = -1;

		if (_boundingSphereCount == 0)
		{
			_cullingGroup.Dispose();
			_cullingGroup = null;
		}
	}

	private static CullingGroup GetOrCreateCullingGroup()
	{
		if (_cullingGroup != null)
		{
			return _cullingGroup;
		}

		_cullingGroup = new CullingGroup { targetCamera = _cachedCamera };
		_cullingGroup.SetBoundingSpheres(_boundingSpheres);
		_cullingGroup.SetBoundingSphereCount(_boundingSphereCount);
		_cullingGroup.SetBoundingDistances(UpdateDistances);
		_cullingGroup.SetDistanceReferencePoint(_cachedCamera.transform);
		return _cullingGroup;
	}

	[HarmonyPatch(typeof(LightFlicker), nameof(Awake))]
	[HarmonyPrefix]
	private static bool Awake_Prefix(LightFlicker __instance)
	{
		if (__instance is VPOLightFlicker)
		{
			return true;
		}

		var light = __instance.GetComponent<Light>();
		if (!light || light.type != LightType.Point)
		{
			return true;
		}

		__instance.gameObject.SetActive(false);
		var flicker = __instance.gameObject.AddComponent<VPOLightFlicker>();
		flicker.m_flickerIntensity = __instance.m_flickerIntensity;
		flicker.m_flickerSpeed = __instance.m_flickerSpeed;
		flicker.m_movement = __instance.m_movement;
		flicker.m_ttl = __instance.m_ttl;
		flicker.m_fadeDuration = __instance.m_fadeDuration;
		flicker.m_fadeInDuration = __instance.m_fadeInDuration;
		flicker.m_flashingLightsSetting = __instance.m_flashingLightsSetting;
		flicker.m_accessibilityBrightnessMultiplier = __instance.m_accessibilityBrightnessMultiplier;

		Destroy(__instance);
		__instance.gameObject.SetActive(true);
		return false;
	}

	[HarmonyPatch(typeof(GameCamera), nameof(GameCamera.Awake))]
	[HarmonyPostfix]
	private static void GameCamera_Awake_Postfix(GameCamera __instance)
	{
		_cachedCamera = __instance.m_camera;
		GetOrCreateCullingGroup();
	}

	[HarmonyPatch(typeof(GameCamera), nameof(GameCamera.OnDestroy))]
	[HarmonyPostfix]
	private static void GameCamera_OnDestroy_Postfix(GameCamera __instance)
	{
		if (_cachedCamera == __instance.m_camera)
		{
			_cachedCamera = null;
			if (_cullingGroup != null)
			{
				_cullingGroup.targetCamera = null;
			}
		}
	}
}
