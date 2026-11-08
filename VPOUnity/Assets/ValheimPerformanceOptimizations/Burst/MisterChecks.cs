using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Mathematics;

namespace ValheimPerformanceOptimizations.Burst
{
	public struct MisterSnapshot
	{
		public float3 Position;
		public float Radius;
		public float Top;
	}

	public struct DemisterSnapshot
	{
		public float3 Position;
		public float EndRange;
	}

	[BurstCompile]
	public static unsafe class MisterChecks
	{
		[BurstCompile]
		[return: MarshalAs(UnmanagedType.U1)]
		public static bool IsCompletelyInsideOther(
			MisterSnapshot* snapshots, int count, int selfIndex, float thickness, int* neighbors, int* neighborCount)
		{
			var foundNeighbors = 0;

			var self = snapshots[selfIndex];
			var shellRadius = self.Radius + 0.1f + thickness;
			// particles above Top get clamped down to it, which can move them outside the shell when Top is below
			// the center, so every mister counts as a neighbor then
			var clampLeavesShell = self.Top < self.Position.y;
			for (var i = 0; i < count; i += 1)
			{
				if (i == selfIndex) { continue; }

				var other = snapshots[i];
				var distSq = math.distancesq(self.Position, other.Position);

				var reach = other.Radius + shellRadius;
				if (clampLeavesShell || distSq < reach * reach)
				{
					neighbors[foundNeighbors] = i;
					foundNeighbors += 1;
				}

				if (self.Top >= other.Top) { continue; }

				// dist + radius + thickness < other.Radius  <=>  dist < maxDist, squared since both sides are >= 0
				var maxDist = other.Radius - self.Radius - thickness;
				if (maxDist <= 0f) { continue; }

				if (distSq < maxDist * maxDist)
				{
					*neighborCount = foundNeighbors;
					return true;
				}
			}

			*neighborCount = foundNeighbors;
			return false;
		}

		[BurstCompile]
		[return: MarshalAs(UnmanagedType.U1)]
		public static bool InsideAny(MisterSnapshot* snapshots, int count, in float3 point, float radius)
		{
			for (var i = 0; i < count; i += 1)
			{
				var other = snapshots[i];
				if (point.y - radius >= other.Top) { continue; }

				var maxDist = other.Radius + radius;
				if (maxDist <= 0f) { continue; }

				if (math.distancesq(point, other.Position) < maxDist * maxDist)
				{
					return true;
				}
			}

			return false;
		}

		[BurstCompile]
		[return: MarshalAs(UnmanagedType.U1)]
		public static bool IsInsideAny(MisterSnapshot* snapshots, int* indices, int indexCount, in float3 point)
		{
			for (var i = 0; i < indexCount; i += 1)
			{
				var other = snapshots[indices[i]];
				if (point.y >= other.Top) { continue; }

				if (math.distancesq(point, other.Position) < other.Radius * other.Radius)
				{
					return true;
				}
			}

			return false;
		}

		[BurstCompile]
		[return: MarshalAs(UnmanagedType.U1)]
		public static bool IsInsideOtherDemister(
			DemisterSnapshot* snapshots, int count, int ignoreIndex, in float3 point, float radius)
		{
			for (var i = 0; i < count; i += 1)
			{
				if (i == ignoreIndex) { continue; }

				// dist + radius < EndRange  <=>  dist < maxDist, squared since both sides are >= 0
				var other = snapshots[i];
				var maxDist = other.EndRange - radius;
				if (maxDist <= 0f) { continue; }

				if (math.distancesq(point, other.Position) < maxDist * maxDist)
				{
					return true;
				}
			}

			return false;
		}
	}
}
