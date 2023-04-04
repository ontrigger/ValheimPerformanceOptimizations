Shader "VPO/DepthOnly" {
	Properties {
		_MainTex ("Texture", 2D) = "white" {}
	}
	SubShader {
		Tags { "RenderType"="Opaque" }

	    Pass {
            ZWrite On

            CGPROGRAM
            #pragma target 4.5

            #pragma shader_feature _ALPHATEST_ON _ALPHABLEND_ON _ALPHAPREMULTIPLY_ON
            #pragma shader_feature _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma multi_compile_instancing

            #pragma vertex vert
            #pragma fragment fragShadowCaster

            #include "UnityStandardShadow.cginc"

            void vert(VertexInput v, out float4 opos : SV_POSITION, out VertexOutputShadowCaster o)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                opos = UnityObjectToClipPos(v.vertex.xyz);
                o.tex = TRANSFORM_TEX(v.uv0, _MainTex);
            }
            
            ENDCG
        }		
    }
    SubShader {
		Tags { "RenderType"="TransparentCutout" }

	    Pass {
            ZWrite On

            CGPROGRAM
            #pragma target 4.5

            #pragma shader_feature _ALPHATEST_ON _ALPHABLEND_ON _ALPHAPREMULTIPLY_ON
            #pragma shader_feature _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma multi_compile_instancing

            #pragma vertex vert
            #pragma fragment fragShadowCaster

            #include "UnityStandardShadow.cginc"

            void vert(VertexInput v, out float4 opos : SV_POSITION, out VertexOutputShadowCaster o)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                opos = UnityObjectToClipPos(v.vertex.xyz);
                o.tex = TRANSFORM_TEX(v.uv0, _MainTex);
            }
            
            ENDCG
        }		
    }
	FallBack "Off"
}