using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace VPOBurst
{
	[BurstCompile]
	public struct GenerateCollisionVerticesJob : IJob
	{
		[ReadOnly] public int Width;
		[ReadOnly] public float Scale;

		[ReadOnly] public NativeArray<float> Heights;

		[WriteOnly] public NativeArray<Vector3> Verts;

		[WriteOnly] public NativeArray<float> MinMax;

		public void Execute()
		{
			var w1 = Width + 1;
			var offset = Width * Scale * -0.5f;
			var minHeight = float.MaxValue;
			var maxHeight = float.MinValue;

			for (var index = 0; index < Verts.Length; index++)
			{
				var i = index / w1;
				var j = index % w1;
				var y = Heights[i * w1 + j];
				Verts[index] = new Vector3(offset + j * Scale, y, offset + i * Scale);

				minHeight = math.min(minHeight, y);
				maxHeight = math.max(maxHeight, y);
			}

			MinMax[0] = minHeight;
			MinMax[1] = maxHeight;
		}
	}

	[BurstCompile]
	public struct GenerateCollisionIndicesJob : IJobParallelFor
	{
		[ReadOnly] public int Width;

		[WriteOnly] public NativeArray<int> Indices;

		public void Execute(int quadIndex)
		{
			var k = quadIndex / Width;
			var l = quadIndex % Width;
			var w1 = Width + 1;

			var i0 = k * w1 + l;
			var i1 = i0 + 1;
			var i2 = i0 + w1 + 1;
			var i3 = i0 + w1;

			var dst = quadIndex * 6;
			Indices[dst] = i0;
			Indices[dst + 1] = i3;
			Indices[dst + 2] = i1;
			Indices[dst + 3] = i1;
			Indices[dst + 4] = i3;
			Indices[dst + 5] = i2;
		}
	}
}
