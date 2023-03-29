Shader "VPO/DepthOnly" {
	Properties {
		
	}
	SubShader {
		Tags { "RenderType"="Opaque" }

	    Pass {
            ZWrite On

            CGPROGRAM
            #pragma target 4.5

            #pragma shader_feature_local _ _ALPHATEST_ON _ALPHABLEND_ON _ALPHAPREMULTIPLY_ON
            #pragma shader_feature_local _METALLICGLOSSMAP
            #pragma shader_feature_local _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
            #pragma shader_feature_local _PARALLAXMAP
            #pragma multi_compile_shadowcaster
            #pragma multi_compile_instancing

            #pragma vertex vertShadowCaster
            #pragma fragment fragShadowCaster

            #include "UnityStandardShadow.cginc"
            ENDCG
        }		
		
	}
	FallBack "Off"
}