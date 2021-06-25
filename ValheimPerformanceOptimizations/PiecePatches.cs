using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations
{
    /// <summary>
    ///     The original used Vector3.Distance and caused large spikes in bases
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

    [HarmonyPatch]
    public static class AllComfortPieces
    {
        public static Dictionary<int, Piece> allComfortPieces = new Dictionary<int, Piece>();

        [HarmonyPatch(typeof(Piece), "Awake"), HarmonyPostfix]
        public static void AwakePatch(Piece __instance)
        {
            if (Piece.ghostLayer == 0)
            {
                Piece.ghostLayer = LayerMask.NameToLayer("ghost");
            }

            if (__instance.gameObject.layer != Piece.ghostLayer && __instance.m_comfort != 0)
            {
                allComfortPieces.Add(__instance.GetInstanceID(), __instance);
            }
        }

        [HarmonyPatch(typeof(Piece), "OnDestroy"), HarmonyPostfix]
        public static void OnDestroyPatch(Piece __instance)
        {
            if (allComfortPieces.ContainsKey(__instance.GetInstanceID()))
            {
                allComfortPieces.Remove(__instance.GetInstanceID());
            }
        }

        public static List<Piece> GetNearbyComfortPieces(Vector3 position)
        {
            const float sqrRadius = 10 * 10;
            return allComfortPieces
                   .Where(pair => Vector3.SqrMagnitude(position - pair.Value.transform.position) < sqrRadius)
                   .Select(pair => pair.Value).ToList();
        }
    }
}