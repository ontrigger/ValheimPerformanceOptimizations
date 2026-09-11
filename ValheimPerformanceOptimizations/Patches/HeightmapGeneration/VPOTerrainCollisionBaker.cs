using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches.HeightmapGeneration
{
	[DefaultExecutionOrder(-1000)]
	public class VPOTerrainCollisionBaker : MonoBehaviour
	{
		private static VPOTerrainCollisionBaker _instance;

		private readonly List<BakeData> bakeRequests = new();

		private JobHandle pendingBake;
		private readonly List<BakeData> pendingHeightmaps = new();

		private class BakeData
		{
			public readonly Action<Heightmap> Callback;
			public readonly Heightmap Heightmap;

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
			if (heightmap.IsDistantLod || !heightmap.m_collider) { return false; }

			// can't bake with jobs if all workers are busy
			if (bakeRequests.Count >= JobsUtility.JobWorkerCount) { return false; }

			for (var i = 0; i < bakeRequests.Count; i++)
			{
				if (bakeRequests[i].Heightmap == heightmap)
				{
					return true;
				}
			}

			bakeRequests.Add(new BakeData(heightmap, bakeDoneCallback));
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
			if (bakeRequests.Count == 0)
			{
				return;
			}

			var meshIds = new NativeArray<int>(bakeRequests.Count, Allocator.TempJob);
			for (var i = 0; i < bakeRequests.Count; i++)
			{
				meshIds[i] = bakeRequests[i].Heightmap.m_collisionMesh.GetInstanceID();
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
	}
}
