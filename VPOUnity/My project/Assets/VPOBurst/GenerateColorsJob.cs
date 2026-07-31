using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace VPOBurst
{
	public static class HeightmapBiome
	{
		public const int None = 0;
		public const int Meadows = 1;
		public const int Swamp = 2;
		public const int Mountain = 4;
		public const int BlackForest = 8;
		public const int Plains = 0x10;
		public const int AshLands = 0x20;
		public const int DeepNorth = 0x40;
		public const int Ocean = 0x100;
		public const int Mistlands = 0x200;
	}

	[BurstCompile]
	public struct GenerateColorsJob : IJobParallelFor
	{
		[ReadOnly] public int Width;

		[ReadOnly] [DeallocateOnJobCompletion]
		public NativeArray<int> CornerBiomes;

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
			if ((CornerBiomes[0] | CornerBiomes[1] | CornerBiomes[2] | CornerBiomes[3]) == CornerBiomes[0])
			{
				return GetBiomeColor(CornerBiomes[0]);
			}

			var biomeColor = GetBiomeColor(CornerBiomes[0]);
			var biomeColor2 = GetBiomeColor(CornerBiomes[1]);
			var biomeColor3 = GetBiomeColor(CornerBiomes[2]);
			var biomeColor4 = GetBiomeColor(CornerBiomes[3]);

			var a = Color32.Lerp(biomeColor, biomeColor2, ix);
			var b = Color32.Lerp(biomeColor3, biomeColor4, ix);

			return Color32.Lerp(a, b, iy);
		}

		private static Color32 GetBiomeColor(int biome)
		{
			return biome switch
			{
				HeightmapBiome.Swamp => new Color32(byte.MaxValue, 0, 0, 0),
				HeightmapBiome.Mountain => new Color32(0, byte.MaxValue, 0, 0),
				HeightmapBiome.BlackForest => new Color32(0, 0, byte.MaxValue, 0),
				HeightmapBiome.Plains => new Color32(0, 0, 0, byte.MaxValue),
				HeightmapBiome.AshLands => new Color32(byte.MaxValue, 0, 0, byte.MaxValue),
				HeightmapBiome.DeepNorth => new Color32(0, byte.MaxValue, 0, 0),
				HeightmapBiome.Mistlands => new Color32(0, 0, byte.MaxValue, byte.MaxValue),
				_ => new Color32(0, 0, 0, 0),
			};
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
