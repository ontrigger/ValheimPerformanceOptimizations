using System;
using System.Collections.Generic;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Profiling;

namespace ValheimPerformanceOptimizations.Patches;

[HarmonyPatch]
public sealed class VPOLightFlicker : LightFlicker, IMonoUpdater
{
	private const float BoundingSphereRangeScale = 1.5f;
	private static readonly float[] FlickerDistances = { 50f };

	private static CullingGroup _cullingGroup;
	private static BoundingSphere[] _boundingSpheres = new BoundingSphere[64];
	private static VPOLightFlicker[] _flickers = new VPOLightFlicker[64];
	private static int[] _inRangeIndices = new int[64];
	private static int _count;

	private static readonly ObjectDispatcher Dispatcher = CreateDispatcher();
	private static readonly Dictionary<int, VPOLightFlicker> FlickerByInstanceId = new();

	private int boundingSphereIndex = -1;
	private int inRangeFrame = -1;

	private static ObjectDispatcher CreateDispatcher()
	{
		var dispatcher = new ObjectDispatcher();
		dispatcher.EnableTransformTracking<VPOLightFlicker>(ObjectDispatcher.TransformTrackingType.GlobalTRS);
		return dispatcher;
	}

	new private void OnEnable()
	{
		base.OnEnable();
		Register();
	}

	new private void OnDisable()
	{
		base.OnDisable();
		Unregister();
	}

	void IMonoUpdater.CustomUpdate(float deltaTime, float time)
	{
		if (boundingSphereIndex >= 0 && inRangeFrame != Time.frameCount && m_light.intensity > 0f) { return; }

		Profiler.BeginSample("VPOLightFlicker.CustomUpdate");
		CustomUpdate(deltaTime, time);
		Profiler.EndSample();
	}

	private void Register()
	{
		if (_cullingGroup == null || m_ttl > 0f) { return; }

		if (_count == _boundingSpheres.Length)
		{
			var newCapacity = _count * 2;
			Array.Resize(ref _boundingSpheres, newCapacity);
			Array.Resize(ref _flickers, newCapacity);
			Array.Resize(ref _inRangeIndices, newCapacity);
			_cullingGroup.SetBoundingSpheres(_boundingSpheres);
		}

		// LightLod fades m_light.range between 0 and its base range, so read the base range from it
		var lightLod = GetComponent<LightLod>();
		var range = Mathf.Max(m_light.range, lightLod ? lightLod.m_baseRange : 0f);

		boundingSphereIndex = _count;
		_boundingSpheres[_count] = new BoundingSphere(transform.position, range * BoundingSphereRangeScale);
		_flickers[_count] = this;
		_count += 1;
		_cullingGroup.SetBoundingSphereCount(_count);
		FlickerByInstanceId[GetInstanceID()] = this;
	}

	private void Unregister()
	{
		if (boundingSphereIndex < 0) { return; }

		var index = boundingSphereIndex;
		var last = _count - 1;
		_boundingSpheres[index] = _boundingSpheres[last];
		_flickers[index] = _flickers[last];
		_flickers[index].boundingSphereIndex = index;
		_flickers[last] = null;
		_count = last;
		_cullingGroup.SetBoundingSphereCount(_count);

		boundingSphereIndex = -1;
		FlickerByInstanceId.Remove(GetInstanceID());
	}

	private static void UpdateMovedBoundingSpheres()
	{
		Profiler.BeginSample("VPOLightFlicker.UpdateMovedBoundingSpheres");
		var changes = Dispatcher.GetTransformChangesAndClear<VPOLightFlicker>(
			ObjectDispatcher.TransformTrackingType.GlobalTRS,
			Allocator.TempJob);

		var ids = changes.transformedID;
		var matrices = changes.localToWorldMatrices;
		for (var i = 0; i < ids.Length; i += 1)
		{
			if (FlickerByInstanceId.TryGetValue(ids[i], out var flicker) && flicker.boundingSphereIndex >= 0)
			{
				_boundingSpheres[flicker.boundingSphereIndex].position = matrices[i].GetPosition();
			}
		}

		changes.Dispose();
		Profiler.EndSample();
	}

	private static void MarkInRangeFlickers()
	{
		Profiler.BeginSample("VPOLightFlicker.MarkInRangeFlickers");
		var frame = Time.frameCount;
		var inRangeCount = _cullingGroup.QueryIndices(true, 0, _inRangeIndices, 0);
		for (var i = 0; i < inRangeCount; i += 1)
		{
			var index = _inRangeIndices[i];
			if (index < _count)
			{
				_flickers[index].inRangeFrame = frame;
			}
		}

		Profiler.EndSample();
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
		return false;
	}

	[HarmonyPatch(typeof(MonoUpdaters), nameof(MonoUpdaters.Update))]
	[HarmonyPrefix]
	private static void MonoUpdaters_Update_Prefix()
	{
		if (_cullingGroup == null) { return; }

		UpdateMovedBoundingSpheres();
		MarkInRangeFlickers();
	}

	[HarmonyPatch(typeof(GameCamera), nameof(GameCamera.Awake))]
	[HarmonyPostfix]
	private static void GameCamera_Awake_Postfix(GameCamera __instance)
	{
		if (_cullingGroup == null)
		{
			_cullingGroup = new CullingGroup();
			_cullingGroup.SetBoundingSpheres(_boundingSpheres);
			_cullingGroup.SetBoundingSphereCount(_count);
			_cullingGroup.SetBoundingDistances(FlickerDistances);
		}

		_cullingGroup.targetCamera = __instance.m_camera;
		_cullingGroup.SetDistanceReferencePoint(__instance.m_camera.transform);
	}

	[HarmonyPatch(typeof(GameCamera), nameof(GameCamera.OnDestroy))]
	[HarmonyPostfix]
	private static void GameCamera_OnDestroy_Postfix(GameCamera __instance)
	{
		if (_cullingGroup != null && _cullingGroup.targetCamera == __instance.m_camera)
		{
			_cullingGroup.targetCamera = null;
		}
	}
}
