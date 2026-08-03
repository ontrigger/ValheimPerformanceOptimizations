using System.Runtime.CompilerServices;
using HarmonyLib;
using Unity.Profiling;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches;

/// <summary>
/// non muted out of range audio sources still play because theres max 512 virtual voices
/// so we mute them to not consume cpu
/// -0.5ms overall with 450 active sources
/// </summary>
[HarmonyPatch]
public static class ZSFXVirtualizationPatch
{
	private static readonly ProfilerMarker VirtualizeMarker = new("VPO.ZSFX.Virtualize");

	private class CullState
	{
		public bool Culled;
		public bool WantPlaying;
		public bool Cached;
		public bool IsSpatial;
		public float CullDistanceSqr;
		public Transform Transform;
	}

	private static readonly ConditionalWeakTable<ZSFX, CullState> States = new();

	private static Vector3 _listenerPos;

	[HarmonyPatch(typeof(MonoUpdaters), nameof(MonoUpdaters.Update))]
	[HarmonyPrefix]
	private static void MonoUpdaters_Update_Prefix()
	{
		var camera = Utils.GetMainCamera();
		_listenerPos = camera != null ? camera.transform.position : Vector3.zero;
	}

	private static void EnsureCache(CullState state, ZSFX sfx, AudioSource source)
	{
		if (state.Cached) { return; }

		state.Transform = sfx.transform;
		state.IsSpatial = source.spatialBlend > 0f;
		var maxDistance = source.maxDistance;
		if (maxDistance > 0f)
		{
			var cullDistance = maxDistance + 2f;
			state.CullDistanceSqr = cullDistance * cullDistance;
		}
		else
		{
			state.CullDistanceSqr = 0f;
		}

		state.Cached = true;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool IsBeyondAudibleRange(CullState state)
	{
		if (!state.IsSpatial || state.CullDistanceSqr <= 0f) { return false; }

		return (state.Transform.position - _listenerPos).sqrMagnitude > state.CullDistanceSqr;
	}

	[HarmonyPatch(typeof(ZSFX), nameof(ZSFX.CustomUpdate))]
	[HarmonyPostfix]
	private static void CustomUpdate_Postfix(ZSFX __instance)
	{
		VirtualizeMarker.Begin();

		var audioSource = __instance.m_audioSource;
		if (audioSource == null || !audioSource.loop || __instance.m_delay >= 0f)
		{
			VirtualizeMarker.End();
			return;
		}

		if (__instance.m_fadeOutTimer >= 0f)
		{
			VirtualizeMarker.End();
			return;
		}

		var state = States.GetOrCreateValue(__instance);
		EnsureCache(state, __instance, audioSource);

		var concurrencyMuted = __instance.m_disabledFromConcurrency
			&& __instance.m_concurrencyVolumeModifier <= 0.001f;
		var shouldCull = concurrencyMuted || IsBeyondAudibleRange(state);

		if (shouldCull)
		{
			if (audioSource.isPlaying)
			{
				state.WantPlaying = true;
				state.Culled = true;
				audioSource.Stop();
			}

			VirtualizeMarker.End();
			return;
		}

		if (!state.Culled || !state.WantPlaying || __instance.m_disabledFromConcurrency)
		{
			VirtualizeMarker.End();
			return;
		}

		state.Culled = false;
		audioSource.Play();
		VirtualizeMarker.End();
	}

	[HarmonyPatch(typeof(ZSFX), nameof(ZSFX.Play))]
	[HarmonyPostfix]
	private static void Play_Postfix(ZSFX __instance)
	{
		var audioSource = __instance.m_audioSource;
		if (audioSource == null || !audioSource.loop)
		{
			return;
		}

		var state = States.GetOrCreateValue(__instance);
		state.WantPlaying = true;
		state.Culled = false;
		state.Cached = false;
		EnsureCache(state, __instance, audioSource);
	}

	[HarmonyPatch(typeof(ZSFX), nameof(ZSFX.Stop))]
	[HarmonyPostfix]
	private static void Stop_Postfix(ZSFX __instance)
	{
		if (!States.TryGetValue(__instance, out var state)) { return; }

		state.WantPlaying = false;
		state.Culled = false;
	}

	[HarmonyPatch(typeof(ZSFX), nameof(ZSFX.OnDisable))]
	[HarmonyPostfix]
	private static void OnDisable_Postfix(ZSFX __instance)
	{
		if (!States.TryGetValue(__instance, out var state)) { return; }

		state.Culled = false;
		state.WantPlaying = false;
		state.Cached = false;
		state.Transform = null;
	}
}
