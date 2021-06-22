using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations
{
    /// <summary>
    ///     CalculateComfortLevel used to get all nearby pieces and then sorted them by comfortGroup and name.
    ///     It tired to calculate the max comfort level of a piece in a very complicated way.
    ///     And GetNearbyPieces() + sort was done every time, even if the list wasn't used.
    ///     If a piece is in a comfortGroup, only the highest m_comfort of this group is used. If no comfortGroup is given,
    ///     the name is once added
    /// </summary>
    [HarmonyPatch(typeof(SE_Rested), "CalculateComfortLevel")]
    public static class CalculateComfortLevelPatch
    {
        private static bool Prefix(SE_Rested __instance, Player player, out int __result)
        {
            __result = 1;
            if (!player.InShelter())
            {
                return false;
            }

            // in shelter one extra comfort
            __result += 1;

            List<Piece> nearbyPieces = SE_Rested.GetNearbyPieces(player.transform.position);
            var maxByComfortGroup = new Dictionary<Piece.ComfortGroup, int>();
            var unsorted = new Dictionary<string, Piece>();

            foreach (var piece in nearbyPieces)
            {
                if (piece.m_comfortGroup == 0)
                {
                    if (!unsorted.ContainsKey(piece.m_name))
                    {
                        unsorted.Add(piece.m_name, piece);
                    }
                }
                else
                {
                    if (!maxByComfortGroup.ContainsKey(piece.m_comfortGroup))
                    {
                        maxByComfortGroup.Add(piece.m_comfortGroup, 0);
                    }

                    int currentMax = maxByComfortGroup[piece.m_comfortGroup];
                    maxByComfortGroup[piece.m_comfortGroup] = Mathf.Max(currentMax, piece.m_comfort);
                }
            }

            __result += maxByComfortGroup.Values.Sum() + unsorted.Values.Sum(i => i.m_comfort);

            return false;
        }
    }
}