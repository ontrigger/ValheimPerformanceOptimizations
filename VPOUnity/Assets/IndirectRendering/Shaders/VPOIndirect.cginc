#ifndef VPO_INDIRECT_INCLUDED
#define VPO_INDIRECT_INCLUDED

// Procedural instancing for IndirectRenderer draws. Shaders add
// #pragma instancing_options procedural:VPOSetupIndirect and set
// vpoIndirectInstanceID in their vertex function before
// UNITY_SETUP_INSTANCE_ID. The pixel stage cannot recover the instance, so
// anything it needs from the instance matrices must be passed from vert.

struct VPOIndirectInstanceData
{
	float4x4 objectToWorld;
	// Row 3 is (overrideIndex, batchIndex + 1, 0, 1); restored before use.
	float4x4 worldToObject;
};

#if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
	StructuredBuffer<VPOIndirectInstanceData> _VPOInstanceData;
	StructuredBuffer<uint> _VPOVisibleInstanceIndices;
	// A draw's visible range starts at
	// _VPOVisibleInstanceOffsets[_VPOVisibleInstanceOffsetIndex] + _VPOVisibleViewBase.
	StructuredBuffer<uint> _VPOVisibleInstanceOffsets;
	uint _VPOVisibleInstanceOffsetIndex;
	uint _VPOVisibleViewBase;
	static uint vpoIndirectInstanceID;
	static uint vpoOverrideIndex;

	uint VPOGetIndirectInstanceID(uint svInstanceID)
	{
		return svInstanceID;
	}
#endif

void VPOSetupIndirect()
{
	#if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
		uint visibleOffset = _VPOVisibleInstanceOffsets[_VPOVisibleInstanceOffsetIndex] + _VPOVisibleViewBase;
		uint sourceInstanceID = _VPOVisibleInstanceIndices[visibleOffset + vpoIndirectInstanceID];
		// Row 3 carries the LOD range for the cull kernels.
		unity_ObjectToWorld = _VPOInstanceData[sourceInstanceID].objectToWorld;
		unity_ObjectToWorld[3] = float4(0, 0, 0, 1);
		float4x4 worldToObject = _VPOInstanceData[sourceInstanceID].worldToObject;
		vpoOverrideIndex = (uint)(worldToObject[3].x + 0.5);
		worldToObject[3] = float4(0, 0, 0, 1);
		unity_WorldToObject = worldToObject;
	#endif
}

#endif
