using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace VPOBurst
{
	public struct WaveRequestData
	{
		public Vector3 Position;
		public float Depth;
		public float HeightOffset;
		public Vector4 Wind;
		public Vector4 Wind2;
		public float WindBlend;
		public bool UseGlobalWind;
	}

	[BurstCompile]
	public static class WaterWaves
	{
		[BurstCompile]
		public static float CalcWave(
			in float3 worldPos, float depth, in float4 wind, float waterTime, float waveFactor)
		{
			var dir0 = math.normalize(new float2(wind.x, wind.z));
			var tan0 = new float2(-dir0.y, dir0.x);

			// Mathf.Lerp clamps t; math.lerp does not.
			var depthScale = math.lerp(0f, wind.w, math.saturate(depth));
			var time = waterTime / 20f;

			var sum =
				CreateWave(in worldPos, time, 10f, 0.04f, 8f, dir0, tan0, 0.5f) +
				CreateWave(in worldPos, time, 14.123f, 0.08f, 6f, Dir1, Tan1, 0.5f) +
				CreateWave(in worldPos, time, 22.312f, 0.1f, 4f, Dir2, Tan2, 0.5f) +
				CreateWave(in worldPos, time, 31.42f, 0.2f, 2f, Dir3, Tan3, 0.5f) +
				CreateWave(in worldPos, time, 35.42f, 0.4f, 1f, Dir4, Tan4, 0.5f) +
				CreateWave(in worldPos, time, 38.1223f, 1f, 0.8f, Dir5, Tan5, 0.7f) +
				CreateWave(in worldPos, time, 41.1223f, 1.2f, 0.6f * waveFactor, Dir6, Tan6, 0.8f) +
				CreateWave(in worldPos, time, 51.5123f, 1.3f, 0.4f * waveFactor, Dir7, Tan7, 0.9f) +
				CreateWave(in worldPos, time, 54.2f, 1.3f, 0.3f * waveFactor, Dir8, Tan8, 0.9f) +
				CreateWave(in worldPos, time, 56.123f, 1.5f, 0.2f * waveFactor, Dir9, Tan9, 0.9f);

			return sum * depthScale;
		}

		[BurstCompile]
		public static float CalcWaveBlended(
			in float3 worldPos, float depth,
			in float4 wind1, in float4 wind2, float windBlend,
			float waterTime, float waveFactor)
		{
			if (depth == 0f)
			{
				return 0f;
			}

			if (windBlend == 0f)
			{
				return CalcWave(in worldPos, depth, in wind1, waterTime, waveFactor);
			}

			var a = CalcWave(in worldPos, depth, in wind1, waterTime, waveFactor);
			var b = CalcWave(in worldPos, depth, in wind2, waterTime, waveFactor);
			return math.lerp(a, b, windBlend);
		}

		private static float CreateWave(
			in float3 worldPos, float time, float waveSpeed, float waveLength, float waveHeight,
			float2 dir, float2 tangent, float sharpness)
		{
			var vector = -(worldPos.z * dir + worldPos.x * tangent);
			var num = time * waveSpeed;
			return (TrochSin(num + vector.y * waveLength, sharpness)
					* TrochSin(num * 0.123f + vector.x * 0.13123f * waveLength, sharpness) - 0.2f)
				* waveHeight;
		}

		private static float TrochSin(float x, float k)
		{
			return math.sin(x - math.cos(x) * k) * 0.5f + 0.5f;
		}

		private static readonly float2 Dir1 = math.normalize(new float2(1.0312f, 0.312f));
		private static readonly float2 Tan1 = new float2(-Dir1.y, Dir1.x);
		private static readonly float2 Dir2 = math.normalize(new float2(-0.123f, 1.12f));
		private static readonly float2 Tan2 = new float2(-Dir2.y, Dir2.x);
		private static readonly float2 Dir3 = math.normalize(new float2(0.423f, 0.124f));
		private static readonly float2 Tan3 = new float2(-Dir3.y, Dir3.x);
		private static readonly float2 Dir4 = math.normalize(new float2(0.123f, -0.64f));
		private static readonly float2 Tan4 = new float2(-Dir4.y, Dir4.x);
		private static readonly float2 Dir5 = math.normalize(new float2(-0.523f, -0.64f));
		private static readonly float2 Tan5 = new float2(-Dir5.y, Dir5.x);
		private static readonly float2 Dir6 = math.normalize(new float2(0.223f, 0.74f));
		private static readonly float2 Tan6 = new float2(-Dir6.y, Dir6.x);
		private static readonly float2 Dir7 = math.normalize(new float2(0.923f, -0.24f));
		private static readonly float2 Tan7 = new float2(-Dir7.y, Dir7.x);
		private static readonly float2 Dir8 = math.normalize(new float2(-0.323f, 0.44f));
		private static readonly float2 Tan8 = new float2(-Dir8.y, Dir8.x);
		private static readonly float2 Dir9 = math.normalize(new float2(0.5312f, -0.812f));
		private static readonly float2 Tan9 = new float2(-Dir9.y, Dir9.x);
	}

	[BurstCompile]
	public struct CalculateWavesJob : IJobParallelFor
	{
		[ReadOnly] public NativeArray<WaveRequestData> WaveRequests;
		[ReadOnly] public float Time;

		[WriteOnly] public NativeArray<float> Results;

		public void Execute(int index)
		{
			var request = WaveRequests[index];
			var wave = 0f;

			if (request.UseGlobalWind && request.Depth != 0f)
			{
				var worldPos = (float3)request.Position;
				var wind1 = (float4)request.Wind;
				var wind2 = (float4)request.Wind2;
				wave = WaterWaves.CalcWaveBlended(
					in worldPos, request.Depth, in wind1, in wind2, request.WindBlend, Time, 1f);
			}

			Results[index] = request.HeightOffset + wave;
		}
	}
}
