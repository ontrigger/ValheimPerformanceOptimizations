using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;
using ValheimPerformanceOptimizations.Patches.HeightmapGeneration;

namespace ValheimPerformanceOptimizations.Patches
{
	[DefaultExecutionOrder(-1000)]
	public class VPOTerrainCollisionBaker : MonoBehaviour
	{
		private static VPOTerrainCollisionBaker _instance;

		private readonly List<BakeData> bakeRequests = new List<BakeData>();

		private JobHandle pendingBake;
		private List<BakeData> pendingHeightmaps = new List<BakeData>();
		
		private class BakeData
		{
			public Action<Heightmap> Callback;
			public Heightmap Heightmap;
			
			public BakeData(Heightmap heightmap, Action<Heightmap> callback)
			{
				Heightmap = heightmap;
				Callback = callback;
			}
		}

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

		public bool RequestAsyncCollisionBake(Heightmap heightmap, Action<Heightmap> bakeDoneCallback)
		{
			if (heightmap.m_isDistantLod) { return false; }

			// can't bake with jobs if all workers are busy
			if (bakeRequests.Count >= JobsUtility.JobWorkerCount) { return false; }

			var bakeData = new BakeData(heightmap, bakeDoneCallback);
			if (!bakeRequests.Contains(bakeData))
			{
				bakeRequests.Add(bakeData);
			}

			return true;
		}

		private void Update()
		{
			pendingBake.Complete();
			
			pendingHeightmaps.ForEach(data => data.Callback(data.Heightmap));

			pendingHeightmaps.Clear();
		}

		private void LateUpdate()
		{
			var meshIds = new NativeArray<int>(bakeRequests.Count, Allocator.TempJob);
			for (var i = 0; i < bakeRequests.Count; i++)
			{
				var data = bakeRequests[i];
				if (data.Heightmap == null) { continue; }

				var meshId = data.Heightmap.m_collisionMesh.GetInstanceID();
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
