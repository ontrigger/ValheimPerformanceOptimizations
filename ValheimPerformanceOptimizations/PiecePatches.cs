using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations
{
    /// <summary>
    /// The original used Vector3.Distance and caused large spikes in bases
    /// </summary>
    [HarmonyPatch(typeof(Piece), "GetAllPiecesInRadius")]
    public static class GetAllPiecesPatch
    {
        private static readonly int PieceLayer = LayerMask.GetMask("piece", "piece_nonsolid");

        private static bool Prefix(Piece __instance, Vector3 p, float radius, List<Piece> pieces)
        {
            if (Piece.ghostLayer == 0)
            {
                Piece.ghostLayer = LayerMask.NameToLayer("ghost");
            }
            
            var colliders = Physics.OverlapSphere(p, radius, PieceLayer, QueryTriggerInteraction.Ignore);
            foreach (var collider1 in colliders)
            {
                var piece = collider1.GetComponentInParent<Piece>();
                if (piece)
                {
                    pieces.Add(piece);
                }
            }

            return false;
        }
    }
}