// Opaque shadow caster for the indirect renderer's native multi-draws; see
// MultiDrawCasters, which picks the batches and their parameters, and
// VPOMultiDraw.cginc. Matches Unity's SHADOWS_DEPTH caster: normal offset and
// linear bias, used for sun cascades, spot lights and point cube faces. The
// opaque casters of VPO/Piece, VPO/Standard and VPO/StaticRock differ only in
// culling, which the material sets, and the ripple, read per batch.
Shader "Hidden/VPO/MultiDrawShadowCaster"
{
	Properties
	{
		_Cull ("Culling", Float) = 2
	}
	SubShader
	{
		Pass
		{
			Name "ShadowCaster"
			Tags { "LightMode" = "ShadowCaster" }
			ZWrite On
			ZTest LEqual
			Cull [_Cull]

			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma target 4.5
			#include "UnityCG.cginc"
			// ValheimShaderCommon.cginc's normal helpers use UnpackScaleNormal.
			#include "UnityStandardUtils.cginc"
			#include "ValheimShaderCommon.cginc"
			#include "Assets/IndirectRendering/Shaders/VPOMultiDraw.cginc"

			// Batch parameters: ripple distance, ripple frequency, and 1 to offset
			// the ripple's phase by position as VPO/Piece does.
			float4 vert(uint vertexID : SV_VertexID, VPO_MULTI_DRAW_VISIBLE_INDEX(visibleIndex)) : SV_POSITION
			{
				VPOMultiDrawVertex vertex = VPOLoadMultiDrawVertex(vertexID, visibleIndex);
				float4 parameters = vertex.batchParameters;

				// UnityClipSpaceShadowCasterPos with this instance's matrices.
				float4 worldPosition = mul(vertex.objectToWorld, float4(vertex.position, 1.0));
				// The material shaders displace in object space by the inverse of this.
				float randomPhase = parameters.z * ValheimHash21(worldPosition.xz + worldPosition.zy);
				worldPosition.xyz += ValheimRipple(worldPosition.xyz + randomPhase, parameters.y, parameters.x);
				if (unity_LightShadowBias.z != 0.0)
				{
					float3 worldNormal = normalize(mul(vertex.normal, vertex.worldToObject));
					float3 worldLight = normalize(UnityWorldSpaceLightDir(worldPosition.xyz));
					float shadowCos = dot(worldNormal, worldLight);
					float shadowSine = sqrt(1.0 - shadowCos * shadowCos);
					worldPosition.xyz -= worldNormal * (unity_LightShadowBias.z * shadowSine);
				}

				return UnityApplyLinearShadowBias(mul(UNITY_MATRIX_VP, worldPosition));
			}

			float4 frag() : SV_Target
			{
				return 0;
			}
			ENDCG
		}
	}
}
