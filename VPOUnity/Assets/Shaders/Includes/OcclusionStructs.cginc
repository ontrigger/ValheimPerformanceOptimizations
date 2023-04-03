
#ifndef __INDIRECT_INCLUDE__
#define __INDIRECT_INCLUDE__

struct InstanceData
{
	float3 boundsCenter;         // 3
	float3 boundsExtents;        // 6
	uint instanceId;
};

struct Indirect2x2Matrix
{
	float4 row0;    // 4
	float4 row1;    // 8
};

struct VisibilityData
{
	uint isVisible;				 // 1
	uint instanceId; // 1
};

#endif