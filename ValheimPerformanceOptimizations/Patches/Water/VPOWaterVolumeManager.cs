using System.Collections.Generic;
using HarmonyLib;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using VPOBurst;

namespace ValheimPerformanceOptimizations.Patches.Water
{
	public class VPOWaterVolumeManager : MonoBehaviour
	{
		public static VPOWaterVolumeManager Instance
		{
			get
			{
				if (!_instance)
				{
					var go = new GameObject(nameof(VPOWaterVolumeManager));
					DontDestroyOnLoad(go);
					_instance = go.AddComponent<VPOWaterVolumeManager>();
				}

				return _instance;
			}
		}

		private static VPOWaterVolumeManager _instance;

		private readonly List<FloaterTarget> _floaterTargets = new();
		private readonly List<WaveRequestData> _pendingRequests = new();
		private readonly List<int> _removeIndices = new();

		private NativeArray<WaveRequestData> _waveRequests;
		private NativeArray<float> _results;
		private JobHandle _handle;
		private bool _jobScheduled;

		public void TryScheduleFloaterUpdates()
		{
			if (_jobScheduled) { return; }

			_floaterTargets.Clear();
			_pendingRequests.Clear();

			var wind1 = WaterVolume.s_globalWind1;
			var wind2 = WaterVolume.s_globalWind2;
			var windAlpha = WaterVolume.s_globalWindAlpha;
			var waterTime = WaterVolume.s_wrappedDayTimeSeconds;

			foreach (var waterVolume in WaterVolume.Instances)
			{
				var inWater = waterVolume.m_inWater;
				var count = inWater.Count;
				if (count == 0) { continue; }

				_removeIndices.Clear();

				var heightOffset = waterVolume.transform.position.y + waterVolume.m_surfaceOffset;
				var useGlobalWind = waterVolume.m_useGlobalWind;
				var forceDepth = waterVolume.m_forceDepth;

				for (var i = 0; i < count; i++)
				{
					var waterInteractable = inWater[i];
					if (waterInteractable == null)
					{
						_removeIndices.Add(i);
						continue;
					}

					var xform = waterInteractable.GetTransform();
					if (!xform)
					{
						_removeIndices.Add(i);
						continue;
					}

					var position = xform.position;
					var downwardsOffset = 0f;
					if (forceDepth < 0f && Utils.LengthXZ(position) > 10500f)
					{
						downwardsOffset = 100f;
					}

					_floaterTargets.Add(new FloaterTarget
					{
						Volume = waterVolume,
						Interactable = waterInteractable,
					});

					_pendingRequests.Add(new WaveRequestData
					{
						Position = position,
						Depth = waterVolume.Depth(position),
						HeightOffset = heightOffset - downwardsOffset,
						Wind = wind1,
						Wind2 = wind2,
						WindBlend = windAlpha,
						UseGlobalWind = useGlobalWind,
					});
				}

				for (var r = _removeIndices.Count - 1; r >= 0; r--)
				{
					inWater.RemoveAt(_removeIndices[r]);
				}
			}

			_jobScheduled = true;

			var requestCount = _pendingRequests.Count;
			if (requestCount == 0) { return; }

			_waveRequests = new NativeArray<WaveRequestData>(requestCount, Allocator.TempJob);
			_results = new NativeArray<float>(requestCount, Allocator.TempJob);

			for (var i = 0; i < requestCount; i++)
			{
				_waveRequests[i] = _pendingRequests[i];
			}

			var job = new CalculateWavesJob
			{
				WaveRequests = _waveRequests,
				Time = waterTime,
				Results = _results,
			};

			_handle = job.Schedule(requestCount, 16);
			JobHandle.ScheduleBatchedJobs();
		}

		private void LateUpdate()
		{
			if (!_jobScheduled) { return; }

			_handle.Complete();

			var count = _floaterTargets.Count;
			for (var i = 0; i < count; i++)
			{
				var target = _floaterTargets[i];
				var volume = target.Volume;
				var interactable = target.Interactable;

				if (volume != null && interactable != null && interactable.GetTransform() != null)
				{
					interactable.SetLiquidLevel(_results[i], LiquidType.Water, volume);
				}
			}

			if (_waveRequests.IsCreated)
			{
				_waveRequests.Dispose();
			}

			if (_results.IsCreated)
			{
				_results.Dispose();
			}

			_floaterTargets.Clear();
			_jobScheduled = false;
		}

		private void OnDestroy()
		{
			if (_jobScheduled)
			{
				_handle.Complete();
			}

			if (_waveRequests.IsCreated)
			{
				_waveRequests.Dispose();
			}

			if (_results.IsCreated)
			{
				_results.Dispose();
			}

			if (_instance == this)
			{
				_instance = null;
			}
		}

		private struct FloaterTarget
		{
			public WaterVolume Volume;
			public IWaterInteractable Interactable;
		}
	}

	[HarmonyPatch]
	public static class WaterVolumeManagerPatch
	{
		[HarmonyPatch(typeof(WaterVolume), nameof(WaterVolume.UpdateFloaters))]
		[HarmonyPrefix]
		private static bool UpdateFloaters_Prefix()
		{
			VPOWaterVolumeManager.Instance.TryScheduleFloaterUpdates();
			return false;
		}
	}
}
