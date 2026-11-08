using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine.Profiling;

namespace ValheimPerformanceOptimizations.Patches
{
	/// <summary>
	/// MonoUpdaters passes a profileScope string to every CustomFixedUpdate/CustomUpdate/CustomLateUpdate/UpdateAI call but never uses it.
	/// This patch wraps each call in a profiler sample so every updater type shows up separately in the profiler.
	/// </summary>
	public static class MonoUpdatersProfilerPatch
	{
		static MonoUpdatersProfilerPatch()
		{
			ValheimPerformanceOptimizations.OnInitialized += Initialize;
		}

		private static void Initialize(ConfigFile configFile, Harmony harmony)
		{
#if ENABLE_PROFILER
			harmony.PatchAll(typeof(MonoUpdatersProfilerPatch));
#endif
		}

		[HarmonyPatch(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.CustomFixedUpdate))] [HarmonyPrefix]
		private static bool CustomFixedUpdatePrefix(
			List<IMonoUpdater> container, List<IMonoUpdater> source, string profileScope, float deltaTime)
		{
			Profiler.BeginSample(profileScope);
			container.AddRange(source);
			foreach (var item in container)
			{
				item.CustomFixedUpdate(deltaTime);
			}

			container.Clear();
			Profiler.EndSample();

			return false;
		}

		[HarmonyPatch(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.CustomUpdate))] [HarmonyPrefix]
		private static bool CustomUpdatePrefix(
			List<IMonoUpdater> container, List<IMonoUpdater> source, string profileScope, float deltaTime, float time)
		{
			Profiler.BeginSample(profileScope);
			container.AddRange(source);
			foreach (var item in container)
			{
				item.CustomUpdate(deltaTime, time);
			}

			container.Clear();
			Profiler.EndSample();

			return false;
		}

		[HarmonyPatch(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.CustomLateUpdate))] [HarmonyPrefix]
		private static bool CustomLateUpdatePrefix(
			List<IMonoUpdater> container, List<IMonoUpdater> source, string profileScope, float deltaTime)
		{
			Profiler.BeginSample(profileScope);
			container.AddRange(source);
			foreach (var item in container)
			{
				item.CustomLateUpdate(deltaTime);
			}

			container.Clear();
			Profiler.EndSample();

			return false;
		}

		[HarmonyPatch(typeof(MonoUpdatersExtra), nameof(MonoUpdatersExtra.UpdateAI))] [HarmonyPrefix]
		private static bool UpdateAIPrefix(
			List<IUpdateAI> container, List<IUpdateAI> source, string profileScope, float deltaTime)
		{
			Profiler.BeginSample(profileScope);
			container.AddRange(source);
			foreach (var item in container)
			{
				item.UpdateAI(deltaTime);
			}

			container.Clear();
			Profiler.EndSample();

			return false;
		}

		[HarmonyPatch(typeof(Character), nameof(Character.CustomFixedUpdate))] [HarmonyTranspiler]
		private static IEnumerable<CodeInstruction> CharacterCustomFixedUpdateTranspiler(
			IEnumerable<CodeInstruction> instructions)
		{
			return ProfilerTranspilers.WrapCallsInSamples(instructions, IsVoidGameMethod);
		}

		[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.CustomFixedUpdate))] [HarmonyTranspiler]
		private static IEnumerable<CodeInstruction> HumanoidCustomFixedUpdateTranspiler(
			IEnumerable<CodeInstruction> instructions)
		{
			return ProfilerTranspilers.WrapCallsInSamples(instructions, IsVoidGameMethod);
		}

		/// <summary>
		/// Non-void calls are skipped since they're mostly cheap getters (IsOwner, IsValid, ...)
		/// </summary>
		private static bool IsVoidGameMethod(MethodInfo method)
		{
			return method.ReturnType == typeof(void) && method.DeclaringType?.Assembly == typeof(Character).Assembly;
		}
	}
}
