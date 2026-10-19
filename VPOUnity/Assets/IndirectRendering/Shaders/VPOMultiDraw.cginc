#ifndef VPO_MULTI_DRAW_INCLUDED
#define VPO_MULTI_DRAW_INCLUDED

// Vertex inputs for shadow casters that IndirectRenderer draws through native
// multi-draw; see IndirectRendererSettings.MultiDrawCasterResolver. Every draw
// reads its vertices from the shared caster buffer, whose index buffer the
// GfxPluginMDI plugin binds, so one draw covers many meshes. The instance
// input includes the draw's startInstance, the batch's visible offset within
// the light's view: on D3D the plugin's input layout hook feeds TEXCOORD7 from
// a per-instance identity buffer, since SV_InstanceID leaves startInstance out
// there; on Vulkan SV_InstanceID includes it. A caster's vertex function takes
// both inputs and passes them to VPOLoadMultiDrawVertex:
//
//     float4 vert(uint vertexID : SV_VertexID, VPO_MULTI_DRAW_VISIBLE_INDEX(visibleIndex)) : SV_POSITION

#if defined(SHADER_API_VULKAN)
	#define VPO_MULTI_DRAW_VISIBLE_INDEX(name) uint name : SV_InstanceID
#else
	#define VPO_MULTI_DRAW_VISIBLE_INDEX(name) uint name : TEXCOORD7
#endif

struct VPOMultiDrawCasterVertex
{
	float3 position;
	float3 normal;
};

struct VPOMultiDrawInstanceData
{
	float4x4 objectToWorld;
	// Row 3 is (overrideIndex, batchIndex + 1, 0, 1); restored before use.
	float4x4 worldToObject;
};

struct VPOMultiDrawVertex
{
	float3 position;
	float3 normal;
	float4x4 objectToWorld;
	float3x3 worldToObject;
	// The resolver's parameters for the instance's batch.
	float4 batchParameters;
};

StructuredBuffer<VPOMultiDrawCasterVertex> _VPOCasterVertices;
StructuredBuffer<VPOMultiDrawInstanceData> _VPOInstanceData;
StructuredBuffer<uint> _VPOVisibleInstanceIndices;
uint _VPOVisibleViewBase;
StructuredBuffer<float4> _VPOCasterBatchParameters;

VPOMultiDrawVertex VPOLoadMultiDrawVertex(uint vertexID, uint visibleIndex)
{
	VPOMultiDrawCasterVertex casterVertex = _VPOCasterVertices[vertexID];
	VPOMultiDrawInstanceData instance = _VPOInstanceData[_VPOVisibleInstanceIndices[_VPOVisibleViewBase + visibleIndex]];
	VPOMultiDrawVertex vertex;
	vertex.position = casterVertex.position;
	vertex.normal = casterVertex.normal;
	vertex.objectToWorld = instance.objectToWorld;
	vertex.objectToWorld[3] = float4(0, 0, 0, 1);
	vertex.worldToObject = (float3x3)instance.worldToObject;
	uint batchIndex = (uint)(instance.worldToObject[3].y + 0.5) - 1;
	vertex.batchParameters = _VPOCasterBatchParameters[batchIndex];
	return vertex;
}

#endif
