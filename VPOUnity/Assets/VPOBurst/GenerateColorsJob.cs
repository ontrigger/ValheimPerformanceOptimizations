using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace VPOBurst
{
	[BurstCompile]
	public struct GenerateColorsJob : IJobParallelFor
	{
		[ReadOnly] public int Width;

		[ReadOnly] [DeallocateOnJobCompletion]
		public NativeArray<Color32> CornerColors;

		[WriteOnly] public NativeArray<Color32> Colors;

		public void Execute(int index)
		{
			var w1 = Width + 1;

			var i = math.floor(index / (float)w1);
			var j = index % w1;

			var iy = math.smoothstep(0f, 1f, i / Width);
			var ix = math.smoothstep(0f, 1f, j / (float)Width);

			Colors[index] = GetBiomeColor(ix, iy);
		}

		private Color32 GetBiomeColor(float ix, float iy)
		{
			var a = Color32.Lerp(CornerColors[0], CornerColors[1], ix);
			var b = Color32.Lerp(CornerColors[2], CornerColors[3], ix);

			return Color32.Lerp(a, b, iy);
		}
	}

	/// <summary>
	/// Probe job: flag stays 1 only when Execute runs under Burst ([BurstDiscard] is stripped).
	/// </summary>
	[BurstCompile]
	public struct CheckBurstedJob : IJob
	{
		public NativeArray<int> Flag;

		[BurstDiscard]
		private void MarkNotBursted()
		{
			Flag[0] = 0;
		}

		public void Execute()
		{
			Flag[0] = 1;
			MarkNotBursted();
		}
	}
}
