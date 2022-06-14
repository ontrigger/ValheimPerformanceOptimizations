using System.Collections.Generic;
using HarmonyLib;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches
{
	[DefaultExecutionOrder(-1000)]
	public class VPOTerrainCollisionBaker : MonoBehaviour
	{
		private static VPOTerrainCollisionBaker _instance;

		private readonly List<Heightmap> bakeRequests = new List<Heightmap>();

		private JobHandle pendingBake;
		private List<Heightmap> pendingHeightmaps = new List<Heightmap>();

		public static VPOTerrainCollisionBaker Instance
		{
			get
			{
				if (!_instance)
				{
					var bakeGameObject = new GameObject("VPOTerrainCollisionBaker");
					_instance = bakeGameObject.AddComponent<VPOTerrainCollisionBaker>();
				}

				return _instance;
			}
		}

		public bool RequestAsyncCollisionBake(Heightmap heightmap)
		{
			if (heightmap.m_isDistantLod) { return false; }

			if (bakeRequests.Count >= JobsUtility.JobWorkerCount) { return false; }

			if (!bakeRequests.Contains(heightmap))
			{
				bakeRequests.Add(heightmap);
			}

			return true;
		}

		private void Update()
		{
			pendingBake.Complete();

			foreach (var heightmap in pendingHeightmaps)
			{
				if (heightmap == null) { continue; }

				heightmap.m_collider.sharedMesh = heightmap.m_collisionMesh;
				heightmap.m_dirty = true;
				ThreadedHeightmapCollisionBakePatch.HeightmapFinished[heightmap] = true;
			}

			pendingHeightmaps.Clear();
		}

		private void LateUpdate()
		{
			var meshIds = new NativeArray<int>(bakeRequests.Count, Allocator.TempJob);
			for (var i = 0; i < bakeRequests.Count; i++)
			{
				var heightmap = bakeRequests[i];
				if (heightmap == null) { continue; }

				var meshId = heightmap.m_collisionMesh.GetInstanceID();
				meshIds[i] = meshId;
			}

			var bakeJob = new BakeCollisionJob { MeshIds = meshIds };
			pendingBake = bakeJob.Schedule(meshIds.Length, 1);
			JobHandle.ScheduleBatchedJobs();

			pendingHeightmaps.AddRange(bakeRequests);
			bakeRequests.Clear();
		}

		private struct BakeCollisionJob : IJobParallelFor
		{
			[ReadOnly]
			[DeallocateOnJobCompletion]
			public NativeArray<int> MeshIds;

			public void Execute(int index)
			{
				Physics.BakeMesh(MeshIds[index], false);
			}
		}

		private struct BakeRequest
		{
			private readonly Heightmap owner;
			private JobHandle jobHandle;

			public BakeRequest(Heightmap owner, JobHandle jobHandle)
			{
				this.owner = owner;
				this.jobHandle = jobHandle;
			}

			public void Complete()
			{
				jobHandle.Complete();

				if (owner == null) { return; }

				owner.m_collider.sharedMesh = owner.m_collisionMesh;
				owner.m_dirty = true;
				ThreadedHeightmapCollisionBakePatch.HeightmapFinished[owner] = true;
			}
		}
	}
}
