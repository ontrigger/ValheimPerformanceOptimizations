// IndirectRenderer's draws into DepthCamera's _SkyAlphaTexture. R is the
// distance from the camera to the nearest surface over 100, decoded by
// ValheimOutdoorMaskGrad; the min blend keeps the nearest of vanilla's
// render and ours whether or not the target has a depth buffer.
Shader "Hidden/VPO/SkyDepth"
{
	Properties
	{
		_MainTex ("Albedo (RGB)", 2D) = "white" {}
		_Color ("Color", Color) = (1,1,1,1)
		_Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5
	}

	SubShader
	{
		Pass
		{
			Cull Off
			ZWrite On
			ZTest LEqual
			BlendOp Min
			Blend One One

			CGPROGRAM
			#pragma target 4.5
			#pragma vertex vert
			#pragma fragment frag
			#pragma multi_compile_local __ _VPO_ALPHATEST
			#pragma multi_compile_instancing
			#pragma instancing_options procedural:VPOSetupIndirect

			#include "UnityCG.cginc"
			#include "Assets/IndirectRendering/Shaders/VPOIndirect.cginc"

			sampler2D _MainTex;
			float4 _MainTex_ST;
			float4 _Color;
			float _Cutoff;
			float3 _VPOViewPosition;

			struct Attributes
			{
				float4 vertex : POSITION;
				float2 texcoord : TEXCOORD0;
				UNITY_VERTEX_INPUT_INSTANCE_ID
			};

			struct Varyings
			{
				float4 position : SV_POSITION;
				float3 worldPosition : TEXCOORD0;
				float2 uv : TEXCOORD1;
			};

			Varyings vert(Attributes input)
			{
				#if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
					vpoIndirectInstanceID = VPOGetIndirectInstanceID(UNITY_GET_INSTANCE_ID(input));
				#endif
				UNITY_SETUP_INSTANCE_ID(input);
				Varyings output;
				float4 worldPosition = mul(unity_ObjectToWorld, float4(input.vertex.xyz, 1.0));
				output.position = mul(UNITY_MATRIX_VP, worldPosition);
				output.worldPosition = worldPosition.xyz;
				output.uv = TRANSFORM_TEX(input.texcoord, _MainTex);
				return output;
			}

			float4 frag(Varyings input) : SV_Target
			{
				#if defined(_VPO_ALPHATEST)
					clip(tex2D(_MainTex, input.uv).a * _Color.a - _Cutoff);
				#endif
				return distance(input.worldPosition, _VPOViewPosition) * 0.01;
			}
			ENDCG
		}
	}
}
