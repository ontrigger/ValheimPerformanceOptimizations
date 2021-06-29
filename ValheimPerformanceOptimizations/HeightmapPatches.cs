using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations
{
    [HarmonyPatch]
    public static class HeightmapPatches
    {
        private static bool hasGenerationThread;

        // Tuple: heightmap.InstanceID, m_collisionMesh.InstanceID
        private static readonly Queue<Tuple<int, int>> ToBake = new Queue<Tuple<int, int>>();
        private static readonly List<Tuple<int, int>> Ready = new List<Tuple<int, int>>();

        private static void BakeThread()
        {
            while (true)
            {
                Thread.Sleep(0);
                Tuple<int, int> next = null;

                lock (ToBake)
                {
                    if (ToBake.Count > 0)
                    {
                        next = ToBake.Dequeue();
                    }
                }

                if (next == null)
                {
                    continue;
                }

                // bake the current mesh to be used in a MeshCollider
                Physics.BakeMesh(next.Item2, false);

                lock (Ready)
                {
                    Ready.Add(next);
                }
            }
        }

        [HarmonyPatch(typeof(Heightmap), "Awake"), HarmonyPostfix]
        private static void AwakePatch(Heightmap __instance)
        {
            if (!hasGenerationThread)
            {
                new Thread(BakeThread).Start();
                hasGenerationThread = true;
            }

            if ((bool) __instance.m_collider)
            {
                // cookingOptions has to be default, otherwise no pre-baking is possible
                __instance.m_collider.cookingOptions = MeshColliderCookingOptions.CookForFasterSimulation |
                                                       MeshColliderCookingOptions.EnableMeshCleaning |
                                                       MeshColliderCookingOptions.UseFastMidphase |
                                                       MeshColliderCookingOptions.WeldColocatedVertices;
            }
        }

        // check if a mesh is finished backing
        [HarmonyPatch(typeof(Heightmap), "Update"), HarmonyPostfix]
        private static void UpdatePatch(Heightmap __instance)
        {
            int instanceId = __instance.GetInstanceID();

            if ((bool) __instance.m_collider)
            {
                lock (Ready)
                {
                    Tuple<int, int> newMesh = Ready.FirstOrDefault(i => i.Item1 == instanceId);

                    if (newMesh != null)
                    {
                        Ready.Remove(newMesh);

                        __instance.m_collider.sharedMesh = __instance.m_collisionMesh;
                        __instance.m_dirty = true;
                    }
                }
            }
        }

        // remove line: 'm_collider.sharedMesh = m_collisionMesh;'
        // it must not be called yet, no collision data is baked
        [HarmonyPatch(typeof(Heightmap), "RebuildCollisionMesh"), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> RebuildCollisionMeshTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = new List<CodeInstruction>(instructions);

            int foundIndex = -1;
            for (int i = 0; i < code.Count; i++)
            {
                CodeInstruction instruction = code[i];
                if (instruction.ToString().Contains("set_sharedMesh"))
                {
                    foundIndex = i;
                }
            }

            if (foundIndex > -1)
            {
                code.RemoveRange(foundIndex - 4, 5);
            }

            return code.AsEnumerable();
        }

        // enqueue current collision mesh to be baked in the separate thread
        [HarmonyPatch(typeof(Heightmap), "RebuildCollisionMesh"), HarmonyPostfix]
        private static void RebuildCollisionMeshPatch(Heightmap __instance)
        {
            lock (ToBake)
            {
                ToBake.Enqueue(new Tuple<int, int>(__instance.GetInstanceID(), __instance.m_collisionMesh.GetInstanceID()));
            }
        }
    }
}
