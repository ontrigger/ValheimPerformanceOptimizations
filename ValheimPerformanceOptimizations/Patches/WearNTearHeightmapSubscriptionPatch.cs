using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace ValheimPerformanceOptimizations.Patches;

using VPO = ValheimPerformanceOptimizations;

// each unsubscribe call allocates which results in like 0.5gb of allocs on unload
// so we replace the sub with our own
[HarmonyPatch]
internal static class WearNTearHeightmapSubscriptionPatch
{
	private sealed class ConnectedPieces
	{
		public readonly HashSet<WearNTear> Pieces = new();
		public readonly Action ClearAll;

		public ConnectedPieces()
		{
			ClearAll = ClearPieces;
		}

		private void ClearPieces()
		{
			var buffer = ClearBufferPool.Count > 0 ? ClearBufferPool.Pop() : new List<WearNTear>();
			buffer.AddRange(Pieces);
			try
			{
				foreach (var piece in buffer)
				{
					piece.ClearCachedSupport();
				}
			}
			finally
			{
				buffer.Clear();
				ClearBufferPool.Push(buffer);
			}
		}
	}

	private static readonly ConditionalWeakTable<Heightmap, ConnectedPieces> ConnectedPiecesByHeightmap = new();
	private static readonly Stack<List<WearNTear>> ClearBufferPool = new();

	private static readonly MethodInfo ClearCachedSupportMethod =
		AccessTools.Method(typeof(WearNTear), nameof(WearNTear.ClearCachedSupport));

	private static readonly EventInfo ClearCacheEvent =
		typeof(Heightmap).GetEvent(nameof(Heightmap.m_clearConnectedWearNTearCache));

	[HarmonyTranspiler]
	[HarmonyPatch(typeof(WearNTear), "Start")]
	private static IEnumerable<CodeInstruction> StartTranspiler(IEnumerable<CodeInstruction> instructions)
	{
		return Redirect(instructions, ClearCacheEvent.GetAddMethod(),
			AccessTools.Method(typeof(WearNTearHeightmapSubscriptionPatch), nameof(Subscribe)), "Start");
	}

	[HarmonyTranspiler]
	[HarmonyPatch(typeof(WearNTear), "OnDestroy")]
	private static IEnumerable<CodeInstruction> OnDestroyTranspiler(IEnumerable<CodeInstruction> instructions)
	{
		return Redirect(instructions, ClearCacheEvent.GetRemoveMethod(),
			AccessTools.Method(typeof(WearNTearHeightmapSubscriptionPatch), nameof(Unsubscribe)), "OnDestroy");
	}

	private static IEnumerable<CodeInstruction> Redirect(
		IEnumerable<CodeInstruction> instructions, MethodInfo target, MethodInfo replacement, string methodName)
	{
		var replaced = 0;
		foreach (var instruction in instructions)
		{
			if (instruction.Calls(target))
			{
				instruction.opcode = OpCodes.Call;
				instruction.operand = replacement;
				replaced += 1;
			}

			yield return instruction;
		}

		if (replaced == 0)
		{
			VPO.Logger.LogWarning(
				$"WearNTear.{methodName}: heightmap event accessor not found, keeping vanilla subscription");
		}
	}

	private static void Subscribe(Heightmap heightmap, Action action)
	{
		if (!TryGetPiece(action, out var piece))
		{
			heightmap.m_clearConnectedWearNTearCache += action;
			return;
		}

		if (!ConnectedPiecesByHeightmap.TryGetValue(heightmap, out var connected))
		{
			connected = new ConnectedPieces();
			ConnectedPiecesByHeightmap.Add(heightmap, connected);
			heightmap.m_clearConnectedWearNTearCache += connected.ClearAll;
		}

		connected.Pieces.Add(piece);
	}

	private static void Unsubscribe(Heightmap heightmap, Action action)
	{
		if (!TryGetPiece(action, out var piece))
		{
			heightmap.m_clearConnectedWearNTearCache -= action;
			return;
		}

		if (ConnectedPiecesByHeightmap.TryGetValue(heightmap, out var connected))
		{
			connected.Pieces.Remove(piece);
		}
	}

	private static bool TryGetPiece(Action action, out WearNTear piece)
	{
		piece = action?.Target as WearNTear;
		return piece != null && action.Method == ClearCachedSupportMethod;
	}
}
