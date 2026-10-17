using System.Collections.Generic;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches
{
	/// <summary>
	/// Character.UpdateWalking pushes ~8 animator bools through ZSyncAnimation.SetBool every physics step,
	/// and each one does an Animator.GetBool round trip even though the value almost never changes.
	/// Character.UpdateGroundTilt does the same with an unconditional Animator.SetFloat for the tilt parameter.
	/// This patch caches the last written values on our side and skips the animator call when nothing changed.
	/// </summary>
	public static class AnimatorParameterCachePatch
	{
		private const float TiltEpsilon = 0.0001f;

		private static readonly int[] CachedBoolHashes =
		{
			ZSyncAnimation.GetHash("inWater"),
			ZSyncAnimation.GetHash("onGround"),
			ZSyncAnimation.GetHash("encumbered"),
			ZSyncAnimation.GetHash("flying"),
			ZSyncAnimation.GetHash("slipping"),
			ZSyncAnimation.GetHash("skating"),
			ZSyncAnimation.GetHash("skategliding"),
			ZSyncAnimation.GetHash("falling"),
		};

		// low byte: which bools are known, second byte: their values
		private static readonly Dictionary<int, int> BoolBitsByInstanceId = new();

		private static readonly Dictionary<int, float> LastTiltByAnimatorId = new();

		static AnimatorParameterCachePatch()
		{
			ValheimPerformanceOptimizations.OnInitialized += Initialize;
		}

		private static void Initialize(ConfigFile configFile, Harmony harmony)
		{
			harmony.PatchAll(typeof(AnimatorParameterCachePatch));
		}

		[HarmonyPatch(typeof(ZSyncAnimation), nameof(ZSyncAnimation.SetBool), typeof(int), typeof(bool))]
		[HarmonyPrefix]
		private static bool SetBoolPrefix(ZSyncAnimation __instance, int hash, bool value)
		{
			var bit = GetCachedBoolBit(hash);
			// non-owners get their values overwritten by SyncParameters, so the cache is only valid for owners
			if (bit == 0 || !__instance.m_nview.IsOwner())
			{
				return true;
			}

			var id = __instance.GetInstanceID();
			BoolBitsByInstanceId.TryGetValue(id, out var entry);

			var known = entry & 0xFF;
			var values = entry >> 8;
			if ((known & bit) != 0 && ((values & bit) != 0) == value)
			{
				return false;
			}

			known |= bit;
			values = value ? values | bit : values & ~bit;
			BoolBitsByInstanceId[id] = known | (values << 8);

			return true;
		}

		[HarmonyPatch(typeof(ZSyncAnimation), nameof(ZSyncAnimation.SyncParameters))] [HarmonyPostfix]
		private static void SyncParametersPostfix(ZSyncAnimation __instance)
		{
			if (!__instance.m_nview.IsOwner())
			{
				BoolBitsByInstanceId.Remove(__instance.GetInstanceID());
			}
		}

		[HarmonyPatch(typeof(ZSyncAnimation), nameof(ZSyncAnimation.OnDisable))] [HarmonyPostfix]
		private static void ZSyncAnimationOnDisablePostfix(ZSyncAnimation __instance)
		{
			BoolBitsByInstanceId.Remove(__instance.GetInstanceID());
		}

		[HarmonyPatch(typeof(Character), nameof(Character.OnDisable))] [HarmonyPostfix]
		private static void CharacterOnDisablePostfix(Character __instance)
		{
			if (__instance.m_animator != null)
			{
				LastTiltByAnimatorId.Remove(__instance.m_animator.GetInstanceID());
			}
		}

		[HarmonyPatch(typeof(Character), nameof(Character.UpdateGroundTilt))] [HarmonyTranspiler]
		private static IEnumerable<CodeInstruction> UpdateGroundTiltTranspiler(IEnumerable<CodeInstruction> instructions)
		{
			var animatorSetFloat = AccessTools.Method(typeof(Animator), nameof(Animator.SetFloat), new[] { typeof(int), typeof(float) });
			var setTiltCached = AccessTools.Method(typeof(AnimatorParameterCachePatch), nameof(SetTiltCached));

			foreach (var instruction in instructions)
			{
				if (instruction.Calls(animatorSetFloat))
				{
					instruction.opcode = OpCodes.Call;
					instruction.operand = setTiltCached;
				}

				yield return instruction;
			}
		}

		private static void SetTiltCached(Animator animator, int hash, float value)
		{
			var id = animator.GetInstanceID();
			if (LastTiltByAnimatorId.TryGetValue(id, out var cached) && Mathf.Abs(cached - value) < TiltEpsilon)
			{
				return;
			}

			LastTiltByAnimatorId[id] = value;
			animator.SetFloat(hash, value);
		}

		private static int GetCachedBoolBit(int hash)
		{
			for (var i = 0; i < CachedBoolHashes.Length; i += 1)
			{
				if (CachedBoolHashes[i] == hash)
				{
					return 1 << i;
				}
			}

			return 0;
		}

	}
}
