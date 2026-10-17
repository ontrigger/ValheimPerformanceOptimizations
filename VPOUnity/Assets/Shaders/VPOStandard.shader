Shader "VPO/Standard"
{
	// The opaque and cutout modes of Unity's built-in Standard (metallic setup),
	// used by item meshes on pickables and similar props. PieceMaterialReplacer
	// leaves materials with fade/transparent blending, parallax, detail maps or
	// disabled highlights/reflections on Unity's path.
	Properties
	{
		_Color ("Color", Color) = (1,1,1,1)
		_MainTex ("Albedo", 2D) = "white" {}
		_Cutoff ("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
		_Glossiness ("Smoothness", Range(0.0, 1.0)) = 0.5
		_GlossMapScale ("Smoothness Scale", Range(0.0, 1.0)) = 1.0
		[Gamma] _Metallic ("Metallic", Range(0.0, 1.0)) = 0.0
		_MetallicGlossMap ("Metallic", 2D) = "white" {}
		_BumpScale ("Scale", Float) = 1.0
		[Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
		_OcclusionStrength ("Strength", Range(0.0, 1.0)) = 1.0
		_OcclusionMap ("Occlusion", 2D) = "white" {}
		_EmissionColor ("Color", Color) = (0,0,0)
		_EmissionMap ("Emission", 2D) = "white" {}
	}

	SubShader
	{
		Tags { "RenderType"="Opaque" "Queue"="Geometry" }
		LOD 300

		CGPROGRAM
		#pragma target 4.5
		#pragma surface surf Standard fullforwardshadows addshadow vertex:vert
		// Standard's shader_feature toggles. The bundle has no materials, so
		// variants with these on would be stripped from it; PieceMaterialReplacer
		// copies them from the source material instead.
		#pragma dynamic_branch_local _NORMALMAP
		#pragma dynamic_branch_local _METALLICGLOSSMAP
		#pragma dynamic_branch_local _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
		#pragma dynamic_branch_local _EMISSION
		// Set by PieceMaterialReplacer from the source's _ALPHATEST_ON. Without
		// clip() the G-buffer pass keeps early depth, and the shadow caster drops
		// every texture sample.
		#pragma multi_compile_local __ _VPO_ALPHATEST
		#pragma multi_compile_instancing
		#pragma instancing_options procedural:VPOSetupIndirect
		#pragma multi_compile __ UNITY_HDR_ON
		#pragma multi_compile __ UNITY_LIGHT_PROBE_PROXY_VOLUME

		// UnpackScaleNormal and LerpOneTo.
		#include "UnityStandardUtils.cginc"
		#include "Assets/IndirectRendering/Shaders/VPOIndirect.cginc"

		#define VPO_OVERRIDE_COLOR 1u
		#define VPO_OVERRIDE_EMISSION_COLOR 2u
		#define VPO_OVERRIDE_UV_OFFSET 4u

		// Same layout as VPO/Piece's; see VPOIndirectInstanceOverride.
		struct VPOInstanceOverride
		{
			float4 color;
			float4 emissionColor;
			float4 uvOffset;
			float takingAshlandsDamage;
			uint flags;
			uint2 padding;
		};

		#if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
			StructuredBuffer<VPOInstanceOverride> _VPOInstanceOverrides;
		#endif

		sampler2D _MainTex;
		sampler2D _MetallicGlossMap;
		sampler2D _BumpMap;
		sampler2D _OcclusionMap;
		sampler2D _EmissionMap;

		float4 _MainTex_ST;
		float4 _Color;
		float4 _EmissionColor;
		float _Cutoff;
		float _Glossiness;
		float _GlossMapScale;
		float _Metallic;
		float _BumpScale;
		float _OcclusionStrength;

		struct Input
		{
			// Mesh UV with _MainTex_ST and the UV offset applied. Standard samples
			// every map with the albedo's tiling.
			float2 vpoUV;
			// The material color or its per-instance override.
			float4 vpoColor;
			float3 vpoEmissionColor;
		};

		void vert(inout appdata_full vertex, out Input output)
		{
			#if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
				vpoIndirectInstanceID = VPOGetIndirectInstanceID(UNITY_GET_INSTANCE_ID(vertex));
			#endif
			UNITY_SETUP_INSTANCE_ID(vertex);
			UNITY_INITIALIZE_OUTPUT(Input, output);

			float4 color = _Color;
			float3 emissionColor = _EmissionColor.rgb;
			float2 uvOffset = 0.0;
			#if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
				if (vpoOverrideIndex != 0)
				{
					VPOInstanceOverride instanceOverride = _VPOInstanceOverrides[vpoOverrideIndex];
					if ((instanceOverride.flags & VPO_OVERRIDE_COLOR) != 0)
					{
						color = instanceOverride.color;
					}
					if ((instanceOverride.flags & VPO_OVERRIDE_EMISSION_COLOR) != 0)
					{
						emissionColor = instanceOverride.emissionColor.rgb;
					}
					if ((instanceOverride.flags & VPO_OVERRIDE_UV_OFFSET) != 0)
					{
						uvOffset = instanceOverride.uvOffset.xy;
					}
				}
			#endif
			output.vpoUV = TRANSFORM_TEX(vertex.texcoord.xy, _MainTex) + uvOffset;
			output.vpoColor = color;
			output.vpoEmissionColor = emissionColor;
		}

		void surf(Input input, inout SurfaceOutputStandard output)
		{
			float2 uv = input.vpoUV;
			float4 albedo = tex2D(_MainTex, uv);

			// Mirrors UnityStandardInput's MetallicGloss and Alpha.
			float metallic = _Metallic;
			float smoothness = _Glossiness;
			float alpha = albedo.a * input.vpoColor.a;
			if (_METALLICGLOSSMAP)
			{
				float4 metallicGloss = tex2D(_MetallicGlossMap, uv);
				metallic = metallicGloss.r;
				smoothness = metallicGloss.a * _GlossMapScale;
			}
			if (_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A)
			{
				smoothness = albedo.a * _GlossMapScale;
				alpha = input.vpoColor.a;
			}

			float3 normal = float3(0, 0, 1);
			if (_NORMALMAP)
			{
				normal = UnpackScaleNormal(tex2D(_BumpMap, uv), _BumpScale);
			}

			float3 emission = 0.0;
			if (_EMISSION)
			{
				emission = tex2D(_EmissionMap, uv).rgb * input.vpoEmissionColor;
			}

			output.Albedo = albedo.rgb * input.vpoColor.rgb;
			output.Normal = normal;
			output.Metallic = metallic;
			output.Smoothness = smoothness;
			output.Occlusion = LerpOneTo(tex2D(_OcclusionMap, uv).g, _OcclusionStrength);
			output.Emission = emission;
			output.Alpha = alpha;
			#if defined(_VPO_ALPHATEST)
				clip(alpha - _Cutoff);
			#endif
		}
		ENDCG
	}
}
