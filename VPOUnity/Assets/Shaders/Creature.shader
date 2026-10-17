Shader "VPO/Creature"
{
	// Property names, descriptions, attributes and defaults are copied from the
	// serialized Custom/Creature asset. The descriptions use the markup of
	// Needle's Markdown shader GUI ("#" foldouts, "&&" inline textures).
	Properties
	{
		_ColorFoldout ("# Color", Float) = 0
		_MainTex ("Albedo (RGB)&&", 2D) = "white" {}
		_Color ("Tint", Color) = (1,1,1,1)
		_MainTex_ST ("Tiling and Offset&", Vector) = (1,1,0,0)
		_HSVHeader ("## Color Adjustment", Float) = 0
		_Hue ("-Hue", Range(-0.5,0.5)) = 0
		_Saturation ("-Saturation", Range(-1,1)) = 0
		_Value ("-Value", Range(-1,1)) = 0
		_EmissionSpacer ("## ", Float) = 0
		_EmissionMap ("Glow &&", 2D) = "white" {}
		[HDR] _EmissionColor ("Emissive color", Color) = (0,0,0,1)
		_SmoothnessFoldout ("# Smoothness and Metal", Float) = 0
		_MetallicGlossMap ("Metal Mask&&", 2D) = "white" {}
		_Metallic ("Metallic", Range(0,1)) = 0
		[HDR] _MetalColor ("Color", Color) = (1,1,1,1)
		[MaterialToggle] _UseGlossmap ("Use Glossmap (Metal Alpha)", Float) = 0
		[Space(8)] _Glossiness ("Base Smoothness", Range(0,1)) = 0.5
		_MetalGloss ("Metal Smoothness", Range(0,1)) = 0
		_NormalFoldout ("# Normals", Float) = 0
		_BumpMap ("Normal Map&&", 2D) = "bump" {}
		_BumpScale ("Normal strength", Range(0,10)) = 1
		[MaterialToggle] _TwoSidedNormals ("Double-Sided", Float) = 0
		_MiscFoldout ("# Misc. Settings", Float) = 0
		[Enum(Off,0,Back,2)] _Cull ("Culling", Float) = 2
		_Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5
		[Space(16)] [MaterialToggle] _AddRain ("Add Rain", Float) = 0
		[Toggle(NOISEGLOW)] _NoiseGlowEnabled ("Enable Noise Glow", Float) = 0
		[MaterialToggle] _UseStyles ("Use styles", Float) = 0
		_SnowCover ("Snow Coverage", Range(0,1)) = 0
		_StylesFoldout ("# Styles [_UseStyles > 0]", Float) = 0
		_StyleTex ("Style texture&&", 2D) = "white" {}
		_Style ("Style", Float) = 0
		_NoiseGlowFoldout ("# Noise Glow [NOISEGLOW]", Float) = 0
		[MaterialToggle] _SnapNoiseToPixel ("Snap to Pixel", Float) = 1
		[NoScaleOffset] _NoiseGlowTex ("Texture&", 3D) = "grey" {}
		_NoiseGlowSize ("Size", Float) = 3
		[HDR] _NoiseGlowColor ("Color", Color) = (1,1,1,1)
		_NoiseGlowEdge ("Edge", Range(0,1)) = 0
		// _SkyAlphaTexture, _NoiseTex, _SkyAlphaPosition and _Wet are globals.
	}

	SubShader
	{
		Tags { "RenderType"="Opaque" }
		LOD 200
		Cull [_Cull]

		CGPROGRAM
		// Vanilla has ForwardBase, ForwardAdd, Deferred and ShadowCaster passes, no
		// Meta pass, and no shadow keywords in ForwardAdd. The vanilla-exact
		// reconstruction is ShaderDecomp/Reconstructed/Creature.shader.
		#pragma surface surf Standard vertex:vert addshadow dithercrossfade nometa
		#pragma target 4.5
		// Only fragment programs carry NOISEGLOW; the vertex programs never vary.
		// Vanilla uses shader_feature, but the bundle has no materials, so those
		// variants would be stripped from it.
		#pragma multi_compile_local_fragment __ NOISEGLOW
		// Vanilla always clips (alphatest:_Cutoff). Set by PieceMaterialReplacer
		// only when the albedo alpha can fall below _Cutoff. Without clip() the
		// G-buffer pass keeps early depth, and the shadow caster drops every
		// texture sample.
		#pragma multi_compile_local __ _VPO_ALPHATEST
		#pragma multi_compile_instancing
		#pragma instancing_options procedural:VPOSetupIndirect
		#pragma multi_compile __ UNITY_HDR_ON
		#pragma multi_compile __ UNITY_LIGHT_PROBE_PROXY_VOLUME

		#include "ValheimOutdoor.cginc"
		#include "Assets/IndirectRendering/Shaders/VPOIndirect.cginc"

		sampler2D _MainTex;
		sampler2D _BumpMap;
		sampler2D _MetallicGlossMap;
		sampler2D _StyleTex;
		sampler2D _EmissionMap;

		float4 _MainTex_TexelSize;
		float _BumpScale;
		float _Hue;
		float _Saturation;
		float _Value;
		float _Wet;
		float _Style;
		float _UseStyles;
		float4 _EmissionColor;
		float _Glossiness;
		float _Metallic;
		float _MetalGloss;
		float4 _MetalColor;
		float4 _Color;
		float _AddRain;
		float _TwoSidedNormals;
		float _UseGlossmap;
		float _SnowCover;
		// Declared in every variant: Unity compiles the vertex stage without the
		// fragment's keywords, so a conditional uniform would shift $Globals in
		// the fragment stage only, and the two stages would disagree on offsets.
		float _NoiseGlowEdge;
		float4 _NoiseGlowColor;
		int _SnapNoiseToPixel;
		float _NoiseGlowSize;
		float _Cutoff;

		#if defined(NOISEGLOW)
			sampler3D _NoiseGlowTex;
		#endif

		struct Input
		{
			float2 uv_MainTex;
			float3 worldPos;
			float3 objectPosition;
			float facing : VFACE;
		};

		void vert(inout appdata_full vertex, out Input output)
		{
			#if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
				vpoIndirectInstanceID = VPOGetIndirectInstanceID(UNITY_GET_INSTANCE_ID(vertex));
			#endif
			UNITY_SETUP_INSTANCE_ID(vertex);
			UNITY_INITIALIZE_OUTPUT(Input, output);
			output.objectPosition = vertex.vertex.xyz;
		}

		inline float3 ValheimRGBToHSV(float3 color)
		{
			float4 k = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
			float4 p = lerp(float4(color.bg, k.wz), float4(color.gb, k.xy), step(color.b, color.g));
			float4 q = lerp(float4(p.xyw, color.r), float4(color.r, p.yzx), step(p.x, color.r));
			float d = q.x - min(q.w, q.y);
			float e = 1.0e-10;
			return float3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
		}

		inline float3 ValheimHSVToRGB(float3 hsv)
		{
			float4 k = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
			float3 p = abs(frac(hsv.xxx + k.xyz) * 6.0 - k.www);
			return hsv.z * lerp(k.xxx, saturate(p - k.xxx), hsv.y);
		}

		void surf(Input input, inout SurfaceOutputStandard output)
		{
			float2 uv = input.uv_MainTex;

			float3 normal = lerp(float3(0, 0, 1), UnpackNormal(tex2D(_BumpMap, uv)), _BumpScale);
			// VFACE is +1 on front faces and -1 on back faces.
			normal.z = _TwoSidedNormals > 0.5 ? normal.z * input.facing : normal.z;

			float4 albedoSample = tex2D(_MainTex, uv);
			float4 color = albedoSample * _Color;

			// _StyleTex is a 4x4 atlas of alternative skins.
			if (_UseStyles > 0.5)
			{
				int style = (int)_Style;
				float2 cell = float2((float)(style % 4) * 0.25, (float)(style / 4) * 0.25);
				float2 tileUV = fmod(uv, 1.0);
				tileUV = tileUV < 0.0 ? tileUV + 1.0 : tileUV;
				float4 styleColor = tex2D(_StyleTex, tileUV * 0.25 + cell);
				color.rgb = lerp(color.rgb, styleColor.rgb * _Color.rgb, styleColor.a * _Color.a);
			}

			float4 metalSample = tex2D(_MetallicGlossMap, uv);
			float metallic = metalSample.r * _Metallic;
			// The metal tint applies to alpha too, which feeds the cutoff.
			color = lerp(color, color * _MetalColor, metallic);

			float smoothness;
			if (_UseGlossmap > 0.5)
			{
				float baseGloss = metalSample.a * _Glossiness;
				smoothness = lerp(baseGloss, metalSample.r * _MetalGloss, metallic);
			}
			else
			{
				// Vanilla brightens base smoothness by the untinted albedo.
				float baseGloss = _Glossiness * dot(1.0, albedoSample.rgb) + _Glossiness;
				smoothness = lerp(baseGloss, _MetalGloss, metallic);
			}

			if (input.worldPos.y < 2000.0 && _AddRain > 0.5)
			{
				float outdoor = ValheimOutdoorMask(input.worldPos, _NoiseTex);
				smoothness = lerp(smoothness, 0.8, outdoor * _Wet);
			}

			float3 hsv = ValheimRGBToHSV(color.rgb);
			hsv.x += _Hue;
			hsv.y += _Saturation;
			hsv.z += _Value;
			float3 baseColor = ValheimHSVToRGB(hsv);

			float4 emission = tex2D(_EmissionMap, uv);
			float3 emissionColor = emission.rgb * emission.a * _EmissionColor.rgb;

			#if defined(NOISEGLOW)
				float3 noisePosition = input.objectPosition / _NoiseGlowSize;
				// Moves the lookup to the center of the albedo texel, so the glow
				// matches pixel-art textures.
				float2 snappedUV = floor(uv * _MainTex_TexelSize.zw) / _MainTex_TexelSize.zw
					+ _MainTex_TexelSize.xy * 0.5;
				float2 uvDelta = snappedUV - uv;
				// Inverts the screen-to-UV Jacobian to turn the UV offset into a
				// screen offset.
				float2 uvDdx = ddx(uv);
				float2 uvDdy = ddy(uv);
				float2x2 jacobian = float2x2(uvDdx.x, uvDdy.x, uvDdx.y, uvDdy.y);
				float2x2 inverseJacobian = float2x2(jacobian._22, -jacobian._12, -jacobian._21, jacobian._11)
					* (1.0 / determinant(jacobian));
				float2 screenDelta = mul(inverseJacobian, uvDelta);
				float3 snappedPosition = noisePosition
					+ clamp(ddx(noisePosition) * screenDelta.x + ddy(noisePosition) * screenDelta.y, -1.0, 1.0);
				noisePosition = _SnapNoiseToPixel ? snappedPosition : noisePosition;
				float noise = tex3D(_NoiseGlowTex, noisePosition).r;
				emissionColor += step(noise, _NoiseGlowEdge) * _NoiseGlowColor.rgb * pow(noise, 3.0);
			#endif

			// Snow settles on dark texels and on faces whose bumped normal stays flat.
			float flatSnow = smoothstep(0.95, 1.1, normalize(normal).z) * pow(_SnowCover, 0.25);
			float darkness = lerp(0.1, 1.0, 1.0 - smoothstep(0.0, 0.5, Luminance(baseColor)));
			float snowThreshold = max(darkness - flatSnow, 0.0);
			float snow = smoothstep(0.0, 0.1, _SnowCover - snowThreshold);
			baseColor = lerp(baseColor, 1.0, snow);

			output.Normal = normal;
			output.Albedo = baseColor;
			output.Emission = emissionColor;
			output.Metallic = metallic;
			output.Smoothness = smoothness;
			output.Alpha = color.a;
			#if defined(_VPO_ALPHATEST)
				clip(output.Alpha - _Cutoff);
			#endif
		}
		ENDCG
	}
	CustomEditor "Needle.MarkdownShaderGUI"
}
