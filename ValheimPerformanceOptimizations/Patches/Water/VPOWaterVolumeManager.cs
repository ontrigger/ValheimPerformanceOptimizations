using System.Collections.Generic;
using HarmonyLib;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Profiling;

namespace ValheimPerformanceOptimizations.Patches
{
	public class VPOWaterVolumeManager : MonoBehaviour
	{
		public static VPOWaterVolumeManager Instance 
		{ 
			get
			{
				if (!_instance)
				{
					var bakeGameObject = new GameObject("VPOWaterVolumeManager");
					_instance = bakeGameObject.AddComponent<VPOWaterVolumeManager>();
				}

				return _instance;
			} 
		}

		private readonly List<WaterVolume> volumes = new();

		private readonly List<WaveRequestData> waveLevelRequests = new();

		private JobHandle handle;

		private NativeArray<Vector3> transformPositions;
		private NativeArray<float> heightOffsets;
		private NativeArray<float> depths;
		private NativeArray<Vector4> winds;
		private NativeArray<Vector4> winds2;
		private NativeArray<float> windBlends;

		private NativeArray<float> results;
		
		private static VPOWaterVolumeManager _instance;

		private struct WaveRequestData
		{
			public Vector3 Position;
			
			public float Depth;
			public float HeightOffset;

			public IWaterInteractable WaterInteractable;
			public WaterVolume WaterVolume;

			public Vector4 Wind;
			public Vector4 Wind2;
			public float WindBlend;
		}
		
		private void Update()
		{
			if (ZNetScene.instance == null) { return; }
			
			waveLevelRequests.Clear();
			foreach (var waterVolume in volumes)
			{
				var wind = new Vector4(1f, 0f, 0f, 0f);
				var wind2 = new Vector4(1f, 0f, 0f, 0f);
				var alpha = 0f;
				if (waterVolume.m_useGlobalWind)
				{
					EnvMan.instance.GetWindData(out wind, out wind2, out alpha);
				}
				
				var heightOffset = waterVolume.transform.position.y + waterVolume.m_surfaceOffset;
				foreach (var waterInteractable in waterVolume.m_inWater)
				{
					var xForm = waterInteractable.GetTransform();
					if (!xForm) { continue; };
					
					var position = xForm.position;

					var downwardsOffset = 0f;
					if (Utils.LengthXZ(position) > 10500f && waterVolume.m_forceDepth < 0f)
					{
						downwardsOffset = 100f;
					}

					var request = new WaveRequestData
					{
						WaterInteractable = waterInteractable,
						WaterVolume = waterVolume,
						Depth = waterVolume.Depth(position),
						HeightOffset = heightOffset - downwardsOffset,
						Position = position,
						Wind = wind,
						Wind2 = wind2,
						WindBlend = alpha,
					};
					waveLevelRequests.Add(request);
				}

				waterVolume.m_inWater.RemoveAll(interactable => interactable.GetTransform() == null);
			}
			
			Profiler.BeginSample("alloc and set");
			transformPositions = new NativeArray<Vector3>(waveLevelRequests.Count, Allocator.TempJob);
			heightOffsets = new NativeArray<float>(waveLevelRequests.Count, Allocator.TempJob);
			depths = new NativeArray<float>(waveLevelRequests.Count, Allocator.TempJob);
			winds = new NativeArray<Vector4>(waveLevelRequests.Count, Allocator.TempJob);
			winds2 = new NativeArray<Vector4>(waveLevelRequests.Count, Allocator.TempJob);
			windBlends = new NativeArray<float>(waveLevelRequests.Count, Allocator.TempJob);
			results = new NativeArray<float>(waveLevelRequests.Count, Allocator.TempJob);
			
			for (var i = 0; i < waveLevelRequests.Count; i++)
			{
				var waveLevelRequest = waveLevelRequests[i];
				
				transformPositions[i] = waveLevelRequest.Position;
				heightOffsets[i] = waveLevelRequest.HeightOffset;
				depths[i] = waveLevelRequest.Depth;
				winds[i] = waveLevelRequest.Wind;
				winds2[i] = waveLevelRequest.Wind2;
				windBlends[i] = waveLevelRequest.WindBlend;
			}
			
			var bakeJob = new CalculateWavesJob
			{
				TransformPositions = transformPositions,
				HeightOffsets = heightOffsets,
				Depths = depths,
				Wind = winds,
				Wind2 = winds2,
				WindAlpha = windBlends,
				Time = ZNet.instance.GetWrappedDayTimeSeconds(),
				Results = results,
			};
			Profiler.EndSample();

			Profiler.BeginSample("schedule");
			handle = bakeJob.Schedule(waveLevelRequests.Count, default);
			JobHandle.ScheduleBatchedJobs();
			Profiler.EndSample();
		}

		private void LateUpdate()
		{
			if (!ZNetScene.instance) { return; }
			
			handle.Complete();
			
			Profiler.BeginSample("set stuff");
			for (var i = 0; i < waveLevelRequests.Count; i++)
			{
				var volume = waveLevelRequests[i].WaterVolume;
				var interactable = waveLevelRequests[i].WaterInteractable;
				
				var liquidLevel = results[i];

				if (volume != null && interactable != null && interactable.GetTransform() != null)
				{
					interactable.SetLiquidLevel(liquidLevel, LiquidType.Water, volume);
				}
			}

			if (results.IsCreated)
			{
				results.Dispose();
			}
		}

		public void AddVolume(WaterVolume volume)
		{
			volumes.Add(volume);
		}
		
		public void RemoveVolume(WaterVolume volume)
		{
			volumes.Remove(volume);
		}
	}
	
	internal struct CalculateWavesJob : IJobFor
	{
		[ReadOnly] [DeallocateOnJobCompletion] public NativeArray<Vector3> TransformPositions;
		
		[ReadOnly] [DeallocateOnJobCompletion] public NativeArray<float> Depths;
		[ReadOnly] [DeallocateOnJobCompletion] public NativeArray<float> HeightOffsets;
		
		[ReadOnly] [DeallocateOnJobCompletion] public NativeArray<Vector4> Wind;
		[ReadOnly] [DeallocateOnJobCompletion] public NativeArray<Vector4> Wind2;
		[ReadOnly] [DeallocateOnJobCompletion] public NativeArray<float> WindAlpha;

		public float Time;
		
		[WriteOnly]
		public NativeArray<float> Results;

		public void Execute(int index)
		{
			var worldPos = TransformPositions[index];
			var depth = Depths[index];
			
			var a = CalcWave(worldPos, depth, Wind[index], Time, 1f);
			var b = CalcWave(worldPos, depth, Wind2[index], Time, 1f);
			
			Results[index] = HeightOffsets[index] + Mathf.Lerp(a, b, WindAlpha[index]);
		}
		
		private float CalcWave(Vector3 worldPos, float depth, Vector4 wind, float _WaterTime, float waveFactor)
		{
			var vector = new Vector3(wind.x, wind.y, wind.z);
			var w = wind.w;
			var num = Mathf.Lerp(0f, w, depth);
			var time = _WaterTime / 20f;
			var num2 = CreateWave(worldPos, time, 10f, 0.04f, 8f, new Vector2(vector.x, vector.z), 0.5f);
			var num3 = CreateWave(worldPos, time, 14.123f, 0.08f, 6f, new Vector2(1.0312f, 0.312f), 0.5f);
			var num4 = CreateWave(worldPos, time, 22.312f, 0.1f, 4f, new Vector2(-0.123f, 1.12f), 0.5f);
			var num5 = CreateWave(worldPos, time, 31.42f, 0.2f, 2f, new Vector2(0.423f, 0.124f), 0.5f);
			var num6 = CreateWave(worldPos, time, 35.42f, 0.4f, 1f, new Vector2(0.123f, -0.64f), 0.5f);
			var num7 = CreateWave(worldPos, time, 38.1223f, 1f, 0.8f, new Vector2(-0.523f, -0.64f), 0.7f);
			var num8 = CreateWave(worldPos, time, 41.1223f, 1.2f, 0.6f * waveFactor, new Vector2(0.223f, 0.74f), 0.8f);
			var num9 = CreateWave(worldPos, time, 51.5123f, 1.3f, 0.4f * waveFactor, new Vector2(0.923f, -0.24f), 0.9f);
			var num10 = CreateWave(worldPos, time, 54.2f, 1.3f, 0.3f * waveFactor, new Vector2(-0.323f, 0.44f), 0.9f);
			var num11 = CreateWave(worldPos, time, 56.123f, 1.5f, 0.2f * waveFactor, new Vector2(0.5312f, -0.812f), 0.9f);
			return (num2 + num3 + num4 + num5 + num6 + num7 + num8 + num9 + num10 + num11) * num;
		}
		
		private float CreateWave(Vector3 worldPos, float time, float waveSpeed, float waveLength, float waveHeight, Vector2 dir2d, float sharpness)
		{
			var normalized = new Vector3(dir2d.x, 0f, dir2d.y).normalized;
			var vector = Vector3.Cross(normalized, Vector3.up);
			var vector2 = -(worldPos.z * normalized + worldPos.x * vector);
			return (TrochSin(time * waveSpeed + vector2.z * waveLength, sharpness) * TrochSin(time * waveSpeed * 0.123f + vector2.x * 0.13123f * waveLength, sharpness) - 0.2f) * waveHeight;
		}
		
		private float TrochSin(float x, float k)
		{
			return Mathf.Sin(x - Mathf.Cos(x) * k) * 0.5f + 0.5f;
		}
	}

	[HarmonyPatch]
	public static class WaterVolumeManagerPatch
	{
		[HarmonyPatch(typeof(WaterVolume), nameof(WaterVolume.Awake)), HarmonyPostfix]
		private static void Awake_Postfix(WaterVolume __instance)
		{
			VPOWaterVolumeManager.Instance.AddVolume(__instance);
		}
		
		[HarmonyPatch(typeof(WaterVolume), nameof(WaterVolume.OnDestroy)), HarmonyPostfix]
		private static void OnDestroy_Postfix(WaterVolume __instance)
		{
			VPOWaterVolumeManager.Instance.RemoveVolume(__instance);
		}
		
		[HarmonyPatch(typeof(WaterVolume), nameof(WaterVolume.UpdateFloaters)), HarmonyPrefix]
		private static bool UpdateFloaters_Prefix(WaterVolume __instance)
		{
			return false;
		}
	}
}