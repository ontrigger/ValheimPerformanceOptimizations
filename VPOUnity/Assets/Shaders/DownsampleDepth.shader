Shader "VPO/DownsampleDepth"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        
        Pass
        {
            ZWrite On Blend Off Cull Off ZTest Always

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag

            Texture2D _MainTex;
            SamplerState sampler_MainTex;
            
            struct attributes
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct varyings
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            varyings vert(attributes input)
            {
                varyings output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }

            float4 frag(varyings input) : SV_Target
            {
                float4 r = _MainTex.GatherRed(sampler_MainTex, input.uv);
                float minimum = max(max(max(r.x, r.y), r.z), r.w);
                
                return float4(minimum, 0, 0, 1.0);
            }
            
            ENDCG
        }
        
        // blit from CameraDepthTexture
        Pass
        {
            ZWrite On Blend Off Cull Off ZTest Always

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag

            Texture2D _CameraDepthTexture;
            SamplerState sampler_CameraDepthTexture;
            
            struct attributes
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct varyings
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            varyings vert(attributes input)
            {
                varyings output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }

            float4 frag(varyings input) : SV_Target
            {
                return _CameraDepthTexture.Sample(sampler_CameraDepthTexture, input.uv).r;
            }
            
            ENDCG
        }
    }


    Fallback Off
}