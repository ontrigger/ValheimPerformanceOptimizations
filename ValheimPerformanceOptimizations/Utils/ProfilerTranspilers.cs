using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine.Profiling;

namespace ValheimPerformanceOptimizations
{
	internal static class ProfilerTranspilers
	{
		/// <summary>
		/// Wraps every matching call with a profiler sample named after the callee.
		/// Calls with the same callee in one method share a sample name, so the profiler aggregates them.
		/// </summary>
		public static IEnumerable<CodeInstruction> WrapCallsInSamples(
			IEnumerable<CodeInstruction> instructions, Func<MethodInfo, bool> shouldWrap)
		{
			var beginSample = AccessTools.Method(typeof(Profiler), nameof(Profiler.BeginSample), new[] { typeof(string) });
			var endSample = AccessTools.Method(typeof(Profiler), nameof(Profiler.EndSample));

			CodeInstruction previous = null;
			foreach (var instruction in instructions)
			{
				// constrained./tail./readonly. prefixes must directly precede the call
				var isPrefixed = previous != null && (previous.opcode == OpCodes.Constrained
				                                      || previous.opcode == OpCodes.Tailcall
				                                      || previous.opcode == OpCodes.Readonly);
				previous = instruction;

				if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
				    && !isPrefixed
				    && instruction.operand is MethodInfo method
				    && shouldWrap(method))
				{
					var sampleName = new CodeInstruction(OpCodes.Ldstr, $"{method.DeclaringType?.Name}.{method.Name}");
					instruction.MoveLabelsTo(sampleName);

					yield return sampleName;
					yield return new CodeInstruction(OpCodes.Call, beginSample);
					yield return instruction;
					yield return new CodeInstruction(OpCodes.Call, endSample);
					continue;
				}

				yield return instruction;
			}
		}
	}
}
