using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace VPOBurst
{
	[StructLayout(LayoutKind.Sequential)]
	public struct PackedCullCell
	{
		public CullingAabb Bounds;
		public int MembershipStart;
		public int MembershipCount;
		public int ShadowMembershipCount;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct PackedCullMembership
	{
		public uint InstanceIndex;
		public uint CameraBatchIndex;
		public int ShadowBatchIndex;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct CullChunk
	{
		public uint MembershipStart;
		public uint MembershipCount;
	}

	/// <summary>
	/// Culls cells and emits deterministic fixed-width membership chunks. The
	/// GPU expands each chunk, so the CPU work is proportional to visible cells
	/// rather than visible memberships.
	/// </summary>
	[BurstCompile]
	public struct CullCellsAndBuildChunksJob : IJob
	{
		[ReadOnly] public NativeArray<PackedCullCell> Cells;
		[ReadOnly] public NativeArray<CullingPlanePacket4> CameraPlanePackets;
		[ReadOnly] public NativeArray<CullingPlanePacket4> ShadowPlanePackets;
		[ReadOnly] public int CellCount;
		[ReadOnly] public int CameraPacketCount;
		[ReadOnly] public int ShadowPacketCount;
		[ReadOnly] public bool BuildShadowChunks;

		[WriteOnly] public NativeArray<CullChunk> CameraChunks;
		[WriteOnly] public NativeArray<CullChunk> ShadowChunks;
		public NativeArray<int> CameraChunkCount;
		public NativeArray<int> ShadowChunkCount;

		public static void Run(ref CullCellsAndBuildChunksJob job)
		{
			job.Run();
		}

		public void Execute()
		{
			var cameraChunkCount = 0;
			var shadowChunkCount = 0;
			for (var cellIndex = 0; cellIndex < CellCount; cellIndex++)
			{
				var cell = Cells[cellIndex];
				var cameraVisible = cell.MembershipCount != 0 &&
					IsVisible(cell.Bounds, CameraPlanePackets, CameraPacketCount);
				var shadowVisible = BuildShadowChunks && cell.ShadowMembershipCount != 0 &&
					IsVisible(cell.Bounds, ShadowPlanePackets, ShadowPacketCount);

				if (cameraVisible)
				{
					AppendChunks(cell.MembershipStart, cell.MembershipCount, CameraChunks, ref cameraChunkCount);
				}

				if (shadowVisible)
				{
					AppendChunks(
						cell.MembershipStart,
						cell.MembershipCount,
						ShadowChunks,
						ref shadowChunkCount);
				}
			}

			CameraChunkCount[0] = cameraChunkCount;
			ShadowChunkCount[0] = shadowChunkCount;
		}

		private static void AppendChunks(
			int membershipStart,
			int membershipCount,
			NativeArray<CullChunk> chunks,
			ref int chunkCount)
		{
			var remaining = membershipCount;
			var offset = membershipStart;
			while (remaining > 0)
			{
				var chunkMembershipCount = math.min(64, remaining);
				chunks[chunkCount++] = new CullChunk
				{
					MembershipStart = (uint)offset,
					MembershipCount = (uint)chunkMembershipCount,
				};
				offset += chunkMembershipCount;
				remaining -= chunkMembershipCount;
			}
		}

		private static bool IsVisible(
			CullingAabb bounds,
			NativeArray<CullingPlanePacket4> packets,
			int packetCount)
		{
			for (var index = 0; index < packetCount; index++)
			{
				var packet = packets[index];
				var distance = packet.Nx * bounds.Center.x +
					packet.Ny * bounds.Center.y +
					packet.Nz * bounds.Center.z +
					packet.D;
				var radius = packet.NxAbs * bounds.Extents.x +
					packet.NyAbs * bounds.Extents.y +
					packet.NzAbs * bounds.Extents.z;
				if (math.any(distance + radius < 0f))
				{
					return false;
				}
			}

			return true;
		}
	}
}
