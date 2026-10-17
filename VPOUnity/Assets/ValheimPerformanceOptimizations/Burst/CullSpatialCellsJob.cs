using Unity.Mathematics;

namespace ValheimPerformanceOptimizations.Burst
{
	public struct CullingAabb
	{
		public float3 Center;
		public float3 Extents;
	}

	// Structure-of-arrays packet matching Unity's GPU-driven FrustumPlaneCuller.
	public struct CullingPlanePacket4
	{
		public float4 Nx;
		public float4 Ny;
		public float4 Nz;
		public float4 D;
		public float4 NxAbs;
		public float4 NyAbs;
		public float4 NzAbs;
	}

}
