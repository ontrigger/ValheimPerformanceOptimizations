using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches
{
    /// <summary>
    ///     The original iterated on all pieces, regardless if they provided comfort or not.
    ///     This caused significant spikes in big bases
    ///     This patch collects and iterates only the pieces that have any comfort
    /// </summary>
    [HarmonyPatch]
    public static class GetNearbyPiecesPatch
    {
        public static readonly Dictionary<int, Piece> AllComfortPieces = new Dictionary<int, Piece>();

        [HarmonyPatch(typeof(Piece), nameof(Piece.Awake)), HarmonyPostfix]
        public static void AwakePatch(Piece __instance)
        {
            if (Piece.ghostLayer == 0)
            {
                Piece.ghostLayer = LayerMask.NameToLayer("ghost");
            }

            if (__instance.gameObject.layer != Piece.ghostLayer && __instance.m_comfort != 0)
            {
                AllComfortPieces.Add(__instance.GetInstanceID(), __instance);
            }
        }

        [HarmonyPatch(typeof(Piece), nameof(Piece.OnDestroy)), HarmonyPostfix]
        public static void OnDestroyPatch(Piece __instance)
        {
            AllComfortPieces.Remove(__instance.GetInstanceID());
        }

        public static List<Piece> GetNearbyComfortPieces(Vector3 position)
        {
            const float sqrRadius = 10 * 10;
            return AllComfortPieces
                   .Where(pair => Vector3.SqrMagnitude(position - pair.Value.transform.position) < sqrRadius)
                   .Select(pair => pair.Value).ToList();
        }
    }
}