// Copies Unity's directional cascade split spheres into a buffer the shadow cull
// compute kernels can read. Compute shaders do not receive the UnityShadows
// built-in constants, but a draw issued from the cascade-0 BeforeShadowMapPass
// command buffer does, and they already hold this frame's final values there.
// Pass 1 does the same for the cube face a point light is rendering.
Shader "Hidden/VPO/ShadowCascadeCapture"
{
	SubShader
	{
		Pass
		{
			ZTest Always
			ZWrite Off
			Cull Off
			ColorMask 0

			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma target 4.5
			#include "UnityCG.cginc"

			RWStructuredBuffer<float4> _VPOShadowCascadeCapture : register(u1);

			float4 vert(uint vertexId : SV_VertexID) : SV_POSITION
			{
				return float4(0.0, 0.0, 0.5, 1.0);
			}

			float4 frag(float4 position : SV_POSITION) : SV_Target
			{
				[unroll]
				for (uint cascade = 0; cascade < 4; cascade++)
				{
					_VPOShadowCascadeCapture[cascade] = float4(
						unity_ShadowSplitSpheres[cascade].xyz,
						unity_ShadowSplitSqRadii[cascade]);
				}

				_VPOShadowCascadeCapture[4] = float4(1.0, 0.0, 0.0, 0.0);
				return 0.0;
			}
			ENDCG
		}

		Pass
		{
			ZTest Always
			ZWrite Off
			Cull Off
			ColorMask 0

			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma target 4.5
			#include "UnityCG.cginc"

			// The face index, in cubemap order: +X, -X, +Y, -Y, +Z, -Z.
			RWStructuredBuffer<uint> _VPOShadowFaceCapture : register(u1);

			float4 vert(uint vertexId : SV_VertexID) : SV_POSITION
			{
				return float4(0.0, 0.0, 0.5, 1.0);
			}

			float4 frag(float4 position : SV_POSITION) : SV_Target
			{
				// Clip w is the depth along the view direction, so the last row
				// of the view-projection is the face's forward axis whatever
				// flips the projection applies.
				float3 forward = unity_MatrixVP[3].xyz;
				float3 magnitude = abs(forward);
				uint axis = magnitude.x >= magnitude.y && magnitude.x >= magnitude.z ? 0 :
					magnitude.y >= magnitude.z ? 1 : 2;
				_VPOShadowFaceCapture[0] = axis * 2 + (forward[axis] < 0.0 ? 1 : 0);
				return 0.0;
			}
			ENDCG
		}
	}
}
