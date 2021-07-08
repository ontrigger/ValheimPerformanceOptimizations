using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches
{
    public class VPOTerrainCollisionBaker : MonoBehaviour
    {
        private static readonly Dictionary<Heightmap, int> ImmediateRegenerateRequests = new Dictionary<Heightmap, int>();

        private static readonly Dictionary<Heightmap, int> LateRegenerateRequests = new Dictionary<Heightmap, int>();
        
        public static void RequestCollisionBake(Heightmap heightmap, bool immediate = false)
        {
            if (immediate)
            {
                ImmediateRegenerateRequests[heightmap] = heightmap.m_collisionMesh.GetInstanceID();
                return;
            }
            
            if (!ImmediateRegenerateRequests.ContainsKey(heightmap))
            {
                LateRegenerateRequests[heightmap] = heightmap.m_collisionMesh.GetInstanceID();
            }
        }
        
        private void Update()
        {
            // remove duplicated requests
            foreach (var request in ImmediateRegenerateRequests)
            {
                if (LateRegenerateRequests.TryGetValue(request.Key, out var meshId) && request.Value == meshId)
                {
                    LateRegenerateRequests.Remove(request.Key);
                }
            }
            
            BakeRequested(ImmediateRegenerateRequests);
        }

        private void LateUpdate()
        {
            BakeRequested(LateRegenerateRequests);

            LateRegenerateRequests.Clear();
            ImmediateRegenerateRequests.Clear();
        }

        private static void BakeRequested(Dictionary<Heightmap, int> bakeRequests)
        {
            var meshIds = new NativeArray<int>(bakeRequests.Count, Allocator.TempJob);
            var i = 0;
            foreach (var meshId in bakeRequests.Values)
            {
                meshIds[i] = meshId;
                i++;
            }

            var bakeJob = new BakeCollisionJob {MeshIds = meshIds};
            bakeJob.Schedule(meshIds.Length, 1).Complete();
            meshIds.Dispose();

            foreach (var heightmap in bakeRequests.Keys)
            {
                if (heightmap == null) {continue;}
                
                heightmap.m_collider.sharedMesh = heightmap.m_collisionMesh;
                heightmap.m_dirty = true;
                ThreadedHeightmapCollisionBakePatch.HeightmapFinished[heightmap] = true;
            }
        }

        private struct BakeCollisionJob : IJobParallelFor
        {
            [ReadOnly]
            public NativeArray<int> MeshIds;

            public void Execute(int index)
            {
                Physics.BakeMesh(MeshIds[index], false);
            }
        }
    }
}