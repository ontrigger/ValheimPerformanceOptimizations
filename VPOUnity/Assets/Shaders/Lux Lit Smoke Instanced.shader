Shader "Lux Lit Smoke Instanced" {
	Properties {
		_Color 						("Color", Color) = (1,1,1,1)
		_MainTex 					("Normal (RG) Depth (B) Alpha (A)", 2D) = "white" {}
		
		_TintColor                  ("Tint Color", Color) = (.25,.25,.25,.25)

		[Space(8)]
		[Toggle(EFFECT_HUE_VARIATION)]
		_EnableAlbedo 				("Enable Albedo", Float) = 0
		[NoScaleOffset] _AlbedoMap	("    Albedo (RGB)", 2D) = "white" {}

		[Space(8)]
		[Toggle(_EMISSION)]
		_EnableEmission				("Enable Emission", Float) = 0
		[NoScaleOffset] _EmissionMap("    Emission (RGB) Alpha (A)", 2D) = "black" {}
		_EmissionScale 				("    Emission Scale", Float) = 1

		[Space(8)]
		[Toggle(_FLIPBOOK_BLENDING)]
		_EnableFlipbookBlending		("Enable Flipbook Blending", Float) = 0
		[LuxParticles_HelpDrawer] _HelpFlip ("If enabled you have to adjust the Vertex Stream.", Float) = 0

		[Space(8)]
		_InvFade 					("Soft Particles Factor", Range(0.01,3.0)) = 1.0
		
		[Header(Camera Fade Distances)]
		[Space(4)]
		[LuxParticles_FadeDistancesDrawer]
		_CamFadeDistance 			("Near (X) Far (Y) FarRange (Z) Near Start (W)", Vector) = (4, 150, 25, 0)

		[Header(Lighting)]
		[Space(4)]
		[Toggle(GEOM_TYPE_BRANCH)]
		_AddLightsPerPixel 			("Full Per Pixel Lighting", Float) = 0
		_WrappedDiffuse 			("Wrapped Diffuse", Range(0,1)) = 0.2
		_Translucency 				("Translucency", Range(0,1)) = 0.5
		_AlphaInfluence 			("    Depth Influence", Range(0,4)) = 1

		[Header(Shadows)]
		[Space(4)]
		[Toggle(GEOM_TYPE_FROND)]
		_EnableShadows 				("Enable directional Shadows", Float) = 0
		[Toggle(GEOM_TYPE_MESH)]
		_ShadowsPerPixel 			("    Per Pixel Shadows", Float) = 0
		_ShadowExtrude 				("        Extrude", Range(0, 10)) = 1
		_ShadowDensity 				("Casted Shadows Density", Range(0.5, 2) ) = 1

		[Header(Ambient Lighting)]
		[Space(4)]
		[Toggle(GEOM_TYPE_BRANCH_DETAIL)]
		_PerPixelAmbientLighting	("    Per Pixel Ambient Lighting", Float) = 0
		[LuxParticles_HelpDrawer] _HelpFlip ("Only check if Ambient Source is set to Gradient or Skybox.", Float) = 0
		[Toggle(GEOM_TYPE_LEAF)]
		_LocalAmbientLighting 		("    Enable Light Probes", Float) = 0
		[LuxParticles_HelpDrawer] _HelpFlip ("If enabled you have to add the LuxParticles_LocalAmbientLighting script.", Float) = 0

	}

	SubShader
	{
		Tags{ "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" }

		Pass
		{
			Tags{ "LightMode"="Vertex"}

			Cull Off 
			ZWrite Off
			Blend SrcAlpha OneMinusSrcAlpha
    		ColorMask RGB

			CGPROGRAM
		//	Full Pixel Lighting
			#pragma shader_feature GEOM_TYPE_BRANCH
		//	Directional Shadows
			#pragma shader_feature GEOM_TYPE_FROND
		//	Per Pixel Shadows
			#pragma shader_feature GEOM_TYPE_MESH
		//	Flipbook Blending
			#pragma shader_feature _FLIPBOOK_BLENDING
		//	Albedo
			#pragma shader_feature EFFECT_HUE_VARIATION
		//	Emission
			#pragma shader_feature _EMISSION
		//	Per Pixel Ambient
			#pragma shader_feature GEOM_TYPE_BRANCH_DETAIL
		//	Light Probe Support
			#pragma shader_feature GEOM_TYPE_LEAF

			#pragma vertex vert
			#pragma fragment frag
			#pragma multi_compile_fog
			#pragma multi_compile_particles
			#pragma multi_compile_instancing
			#include "UnityCG.cginc"

			#pragma exclude_renderers d3d9 d3d11_9x
			#pragma target 3.0

			// not needed i think
			#include "UnityStandardParticleInstancing.cginc"

			#include "Includes/LuxParticles_Utils.cginc"
			#include "Includes/LuxParticles_Core.cginc"
			

			fixed4 frag (v2f i) : SV_Target {

				fixed4 normalSample = tex2D(_MainTex, i.uv.xy);
				
				#ifdef _FLIPBOOK_BLENDING
					fixed4 sample2 = tex2D(_MainTex, i.uv.zw);
					normalSample = lerp(normalSample, sample2, i.worldViewDir.w);
				#endif

				fixed3 normal = Lux_UnpackNormalDXT5nm(normalSample.rg);

				fixed4 color = i.color;
				color.a *= normalSample.a;

			//	Add dedicated albedo
				#if defined(EFFECT_HUE_VARIATION)
					fixed3 albedoSample = tex2D(_AlbedoMap, i.uv.xy).rgb;
					#ifdef _FLIPBOOK_BLENDING
						sample2.rgb = tex2D(_AlbedoMap, i.uv.zw).rgb;
						albedoSample.rgb = lerp(albedoSample.rgb, sample2.rgb, i.worldViewDir.w);
					#endif
					color.rgb *= albedoSample.rgb;
				#endif

				#ifdef SOFTPARTICLES_ON
					float sceneZ = LinearEyeDepth (SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(i.projPos)));
					float partZ = i.projPos.z;
					float fade = saturate (_InvFade * (sceneZ-partZ));
					color.a *= fade;
                #endif

                fixed4 AlbedoOut;
				AlbedoOut.a = color.a;
						
			//	Transform Tangent Space Normal to Screen Space
			//	http://mmikkelsen3d.blogspot.de/2011/07/derivative-maps.html		
				float2 dBdUV = normalize( ddx(i.uv.xy) );
				normal.xy = float2(
					( dBdUV.x * normal.x) + (dBdUV.y * normal.y),
			 		(-dBdUV.y * normal.x) + (dBdUV.x * normal.y)
			 	);

			//	Per Pixel Lighting
				
			//	First light
			//	Check if we have a directional light
				UNITY_BRANCH
				if (unity_LightPosition[0].w == 0) {
					#if defined(GEOM_TYPE_FROND)
						#if defined(GEOM_TYPE_MESH)
							float atten = GetLightAttenuation(i.worldPos - i.worldViewDir.xyz * ( 1.0 - normalSample.b) * _ShadowExtrude); // * i.uv.z );
						#else
							float atten = i.vertexLighting.w;
						#endif
					#else
						float atten = 1;
					#endif
					AlbedoOut.rgb = atten * ApplyDirectionalPixelLight (color.rgb, i.LightDirAndAtten0, normal, 0, normalSample.b);
				}
				else {
					AlbedoOut.rgb = ApplyPixelLight (color.rgb, i.LightDirAndAtten0, normal, 0);
				}
				
			//	Three additional Lights
				#if defined(GEOM_TYPE_BRANCH)
					AlbedoOut.rgb += ApplyPixelLight (color.rgb, i.LightDirAndAtten1, normal, 1);
					AlbedoOut.rgb += ApplyPixelLight (color.rgb, i.LightDirAndAtten2, normal, 2);
					AlbedoOut.rgb += ApplyPixelLight (color.rgb, i.LightDirAndAtten3, normal, 3);
				#endif

			//	Vertex and Ambient lighting
				AlbedoOut.rgb += color.rgb * i.vertexLighting.xyz;

			//	Per Pixel Ambient
				#if defined(GEOM_TYPE_BRANCH_DETAIL)
					UNITY_BRANCH
					if ( _Lux_AmbientMode == 1) {
						half3 ambientG = unity_AmbientSky * saturate(normal.y) + unity_AmbientGround * saturate(-normal.y) + unity_AmbientEquator * (1 - abs(normal.y));
						AlbedoOut.rgb += color.rgb * ambientG;
					}
					else {
						#ifdef UNITY_COLORSPACE_GAMMA
							half3 ambientL = Lux_ShadeSH(float4(normal, 1) );
							AlbedoOut.rgb += color.rgb * LinearToGammaSpace(ambientL);
						#else
							AlbedoOut.rgb += color.rgb * Lux_ShadeSH(float4(normal, 1) );
						#endif
					}
				#endif

			//	Emission
				#if defined(_EMISSION)
					fixed4 emissionSample = tex2D(_EmissionMap, i.uv.xy);
					#ifdef _FLIPBOOK_BLENDING
						sample2 = tex2D(_EmissionMap, i.uv.zw);
						emissionSample = lerp(emissionSample, sample2, i.worldViewDir.w);
					#endif
					AlbedoOut.rgb += emissionSample.rgb * _EmissionScale;
					AlbedoOut.a = saturate(AlbedoOut.a + emissionSample.a * i.color.a);
				#endif

			//	Fog
				UNITY_APPLY_FOG(i.fogCoord, AlbedoOut);

				return AlbedoOut;
			}
			ENDCG
		}

	//	SHADOW CASTER
		Pass {
			Name "ShadowCaster"
			Tags { "LightMode" = "ShadowCaster" }
			ZWrite On ZTest LEqual

	
			CGPROGRAM
			#pragma target 3.0
			// TEMPORARY: GLES2.0 temporarily disabled to prevent errors spam on devices without textureCubeLodEXT
			#pragma exclude_renderers gles

			#pragma multi_compile_shadowcaster

			#pragma vertex vertShadowCaster
			#pragma fragment fragShadowCaster

			#define _ALPHABLEND_ON
			//#define _ALPHATEST_ON

			#include "Includes/UnityStandardShadowLike.cginc"

			ENDCG
		}
	}
}
