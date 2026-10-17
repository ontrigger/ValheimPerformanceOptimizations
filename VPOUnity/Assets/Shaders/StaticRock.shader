Shader "VPO/StaticRock"
{
	Properties
	{
		_MainTex ("Albedo (RGB)", 2D) = "white" {}
		_Color ("Color", Color) = (1,1,1,1)
		_EmissiveTex ("Glow", 2D) = "black" {}
		[HDR] _EmissionColor ("Emissive color", Color) = (0,0,0,1)
		_MetalTex ("Metal", 2D) = "white" {}
		_Metallic ("Metallic", Range(0,1)) = 0
		_MetalGloss ("Metal Smoothness", Range(0,1)) = 1
		_GlossMap ("Glossmap", 2D) = "white" {}
		[Toggle(_USEGLOSSMAP_ON)] _UseGlossMap ("Use Glossmap", Float) = 0
		_Glossiness ("Base Smoothness", Range(0,1)) = 0.5
		_MossTex ("Moss (RGB)", 2D) = "white" {}
		_MossAlpha ("Moss alpha", Range(0,1)) = 0
		_MossBlend ("Texture blend", Range(0,1)) = 0
		_MossColor ("Moss Color", Color) = (1,1,1,1)
		_MossGloss ("Moss Smoothness", Range(0,1)) = 0
		_MossNormal ("Normal Blend", Range(0,1)) = 0
		_MossTransition ("Transition Size", Range(0.001,1)) = 0.1
		_BumpMap ("Normal Map", 2D) = "bump" {}
		_BumpScale ("Normal strength", Float) = 1
		[Toggle(_FRESNEL_ON)] _FRESNEL ("Enable Fresnel", Float) = 0
		_FresnelColor ("Fresnel Color", Color) = (0,0,0,0)
		_FresnelPower ("Fresnel Power", Float) = 1
		_WaterlineOffset ("Waterline offset", Float) = 0
		[Toggle] _TriplanarMap ("Use triplanar mapping", Float) = 1
		_TriplanarScale ("Triplanar scale", Float) = 1
		[Toggle(_ADDSNOW_ON)] _AddSnow ("Add Snow", Float) = 0
		[Toggle(_ADDRAIN_ON)] _AddRain ("Add Rain", Float) = 0
		_NoiseTex ("Noise", 2D) = "gray" {}
		_RippleDistance ("Noise distance", Float) = 0
		_RippleFreq ("Noise freq", Float) = 1
		// _SkyAlphaTexture and _SnowGlintOffsetNoise are globals. Declaring them
		// here makes materials bind the property default over the global.
	}

	SubShader
	{
		Tags { "RenderType"="Opaque" "Queue"="Geometry" }
		LOD 300

		CGPROGRAM
		#pragma target 4.5
		#pragma surface surf Standard fullforwardshadows addshadow vertex:vert
		// Vanilla's feature toggles branch on a per-material flag instead of
		// selecting variants. The bundle has no materials, so shader_feature
		// variants with these on were stripped from it. The glossmap toggle is
		// read from _UseGlossMap, as vanilla does.
		#pragma dynamic_branch_local _FRESNEL_ON
		#pragma dynamic_branch_local _ADDSNOW_ON
		#pragma dynamic_branch_local _ADDRAIN_ON
		#pragma multi_compile_instancing
		#pragma instancing_options procedural:VPOSetupIndirect
		#pragma multi_compile __ UNITY_HDR_ON
		#pragma multi_compile __ UNITY_LIGHT_PROBE_PROXY_VOLUME

		#include "ValheimShaderCommon.cginc"
		#include "Assets/IndirectRendering/Shaders/VPOIndirect.cginc"

		sampler2D _MainTex;
		sampler2D _EmissiveTex;
		sampler2D _MetalTex;
		sampler2D _GlossMap;
		sampler2D _MossTex;
		sampler2D _BumpMap;

		float4 _Color;
		float4 _EmissionColor;
		float4 _MossColor;
		float4 _FresnelColor;
		float _Metallic;
		float _MetalGloss;
		float _UseGlossMap;
		float _Glossiness;
		float _MossAlpha;
		float _MossBlend;
		float _MossGloss;
		float _MossNormal;
		float _MossTransition;
		// EnvMan's global wetness.
		float _Wet;
		float _BumpScale;
		float _FresnelPower;
		float _WaterlineOffset;
		float _TriplanarMap;
		float _TriplanarScale;
		float _RippleDistance;
		float _RippleFreq;

		struct Input
		{
			float2 uv_MainTex;
			// Vanilla tiles moss on its own; every other map shares _MainTex_ST.
			float2 uv_MossTex;
			float3 worldPos;
			float3 worldNormal;
			INTERNAL_DATA
		};

		void vert(inout appdata_full vertex)
		{
			#if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
				vpoIndirectInstanceID = VPOGetIndirectInstanceID(UNITY_GET_INSTANCE_ID(vertex));
			#endif
			UNITY_SETUP_INSTANCE_ID(vertex);
			float3 worldPosition = mul(unity_ObjectToWorld, vertex.vertex).xyz;
			float3 displacement = ValheimRipple(worldPosition, _RippleFreq, _RippleDistance);
			vertex.vertex.xyz += mul((float3x3)unity_WorldToObject, displacement);
		}

		void surf(Input input, inout SurfaceOutputStandard output)
		{
			float3 geometricNormal = normalize(WorldNormalVector(input, float3(0, 0, 1)));
			// World-space, to match geometricNormal; the surface shader's viewDir
			// is tangent-space once Normal is written.
			float3 viewDirection = normalize(UnityWorldSpaceViewDir(input.worldPos));
			// Samples inside dynamic branches take these explicit gradients.
			float2 positionDdx = ddx(input.worldPos.xz);
			float2 positionDdy = ddy(input.worldPos.xz);
			float4 albedo = tex2D(_MainTex, input.uv_MainTex);
			float4 packedNormal = tex2D(_BumpMap, input.uv_MainTex);
			float4 metal = tex2D(_MetalTex, input.uv_MainTex);
			float gloss = tex2D(_GlossMap, input.uv_MainTex).r;
			float4 moss = tex2D(_MossTex, input.uv_MossTex);
			float4 emissive = tex2D(_EmissiveTex, input.uv_MainTex);

			if (_TriplanarMap != 0.0)
			{
				float2 uvX, uvY, uvZ, blend;
				ValheimPieceTriplanarCoords(input.worldPos, geometricNormal, _TriplanarScale, uvX, uvY, uvZ, blend);
				albedo = ValheimPieceTriplanarSample(_MainTex, uvX, uvY, uvZ, blend);
				metal = ValheimPieceTriplanarSample(_MetalTex, uvX, uvY, uvZ, blend);
				gloss = ValheimPieceTriplanarSample(_GlossMap, uvX, uvY, uvZ, blend).r;
				moss = ValheimPieceTriplanarSample(_MossTex, uvX, uvY, uvZ, blend);
				emissive = ValheimPieceTriplanarSample(_EmissiveTex, uvX, uvY, uvZ, blend);
				// Vanilla blends the packed texels and unpacks them as a tangent-space normal.
				packedNormal = ValheimPieceTriplanarSample(_BumpMap, uvX, uvY, uvZ, blend);
			}
			float3 normal = UnpackScaleNormal(packedNormal, _BumpScale);
			// Moss and snow pull the normal halfway towards the surface normal.
			float3 flatNormal = lerp(normal, float3(0, 0, 1), 0.5);
			// Moss and snow coverage follow the bumped normal, before either flattens it.
			float bumpedUp = WorldNormalVector(input, normal).y;

			float3 baseColor = albedo.rgb * _Color.rgb;
			float metallic = metal.r * metal.a * _Metallic;
			float smoothness = _UseGlossMap >= 0.5 ? gloss * _Glossiness : _Glossiness * (dot(albedo.rgb, 1.0) + 1.0);
			smoothness = max(smoothness, metallic * _MetalGloss);

			[branch]
			if (_ADDRAIN_ON && input.worldPos.y < 2000.0)
			{
				float outdoor = ValheimOutdoorMaskGrad(input.worldPos, positionDdx, positionDdy, _NoiseTex);
				smoothness = lerp(smoothness, 0.8, outdoor * _Wet);
			}

			// Waterline sits at y = 30: rock below it darkens, a 3 m band above
			// it reads wet, and moss only grows above it.
			float waterline = 30.0 + _WaterlineOffset;
			float underwater = 1.0 - smoothstep(waterline, waterline + 0.2, input.worldPos.y);
			float shoreWetness = smoothstep(waterline - 3.0, waterline, input.worldPos.y) * underwater;

			// Moss gathers in dark texels (_MossBlend) or everywhere (_MossAlpha)
			// on faces whose bumped normal points above _MossNormal.
			float mossTexture = saturate((1.0 - pow(dot(albedo.rgb, 1.0), 0.25)) * _MossBlend);
			float mossSlope = smoothstep(0.0, 1.0, saturate((bumpedUp - _MossNormal) / _MossTransition));
			float mossCoverage = max(mossTexture, _MossAlpha) * mossSlope * (1.0 - underwater);
			float mossMask = moss.a * _MossColor.a * min(mossCoverage * mossCoverage, 1.0);
			baseColor = lerp(baseColor, moss.rgb * _MossColor.rgb, mossMask);
			smoothness = lerp(smoothness, _MossGloss, mossMask);
			metallic *= 1.0 - mossMask;
			normal = lerp(normal, flatNormal, mossMask);

			[branch]
			if (_ADDSNOW_ON && input.worldPos.y < 2000.0)
			{
				float snow = ValheimSnowBiomeMask(input.worldPos) * smoothstep(0.55, 0.7, bumpedUp);
				baseColor = lerp(baseColor, 0.5, snow)
					+ ValheimSnowGlint(input.worldPos, geometricNormal, viewDirection) * snow;
				smoothness = lerp(smoothness, 0.5, snow);
				metallic *= 1.0 - snow;
				normal = lerp(normal, flatNormal, snow);
			}

			output.Normal = normal;
			output.Albedo = baseColor * (1.0 - 0.4 * underwater);
			output.Metallic = metallic;
			output.Smoothness = saturate(lerp(smoothness, 0.8, shoreWetness));
			output.Emission += emissive.rgb * emissive.a * _EmissionColor.rgb;
			if (_FRESNEL_ON)
			{
				output.Emission += _FresnelColor.rgb * pow(1.0 - saturate(dot(viewDirection, geometricNormal)), _FresnelPower);
			}
			output.Alpha = 1.0;
		}
		ENDCG
	}
	Fallback "Standard"
}
