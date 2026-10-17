Shader "VPO/Piece"
{
	Properties
	{
		[Toggle] _TriplanarMap ("Use Triplanar Mapping", Float) = 0
		[Toggle] _TriplanarLocalPos ("Triplanar Uses Local Position", Float) = 0
		_TriplanarScale ("Triplanar Scale", Float) = 1
		_MainTex ("Albedo (RGB)", 2D) = "white" {}
		_Color ("Color", Color) = (1,1,1,1)
		_Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5
		_EmissionMap ("Glow", 2D) = "black" {}
		[HDR] _EmissionColor ("Emissive", Color) = (0,0,0,1)
		_MetallicTex ("Metal Mask", 2D) = "white" {}
		_Metallic ("Metallic", Range(0,1)) = 0
		_MetalColor ("Metal Color", Color) = (1,1,1,1)
		_Glossiness ("Base Smoothness", Range(0,1)) = 0.5
		_MetallicAlphaGloss ("Metal Smoothness", Range(0,1)) = 1
		_BumpMap ("Normal Map", 2D) = "bump" {}
		_BumpScale ("Normal intensity", Float) = 1
		[Toggle(_TWOSIDEDNORMALS_ON)] _TwoSidedNormals ("Double-Sided Normals", Float) = 0
		_NoiseTex ("Noise Texture", 2D) = "gray" {}
		_ValueNoise ("Strength", Float) = 0
		_RippleDistance ("Size", Float) = 0
		_RippleFreq ("Frequency", Float) = 1
		[Toggle(_VALUENOISEVERTEX_ON)] _ValueNoiseVertex ("Value Noise Type", Float) = 0
		_Cull ("Culling", Float) = 2
		[Toggle(_ADDRAIN_ON)] _AddRain ("Add Rain", Float) = 0
		[Toggle(_ADDSNOW_ON)] _AddSnow ("Add Snow", Float) = 0
		[Toggle] _MoveableObject ("Non-Static Object", Float) = 0
		[HideInInspector] _UVOffset ("UV Offset", Vector) = (0,0,0,0)
		[HideInInspector] _TakingAshlandsDamage ("Taking Ashlands Damage", Float) = 0
		// _SkyAlphaTexture, _SnowGlintOffsetNoise and _AshlandsWearTexture are
		// globals. Declaring them here makes materials bind the property default
		// over the global.
	}

	SubShader
	{
		Tags { "RenderType"="Opaque" "Queue"="Geometry" }
		LOD 300
		Cull [_Cull]

		CGPROGRAM
		#pragma target 4.5
		#pragma surface surf Standard fullforwardshadows addshadow vertex:vert
		// Vanilla's feature toggles branch on a per-material flag instead of
		// selecting variants. The bundle has no materials, so shader_feature
		// variants with these on were stripped from it.
		#pragma dynamic_branch_local _TWOSIDEDNORMALS_ON
		#pragma dynamic_branch_local _VALUENOISEVERTEX_ON
		#pragma dynamic_branch_local _ADDRAIN_ON
		#pragma dynamic_branch_local _ADDSNOW_ON
		// Set by PieceMaterialReplacer from _TriplanarMap and _TriplanarLocalPos;
		// only local triplanar needs its own interpolators.
		#pragma multi_compile_local __ _VPO_TRIPLANAR_WORLD _VPO_TRIPLANAR_LOCAL
		// Set by PieceMaterialReplacer only when the albedo alpha can fall below
		// _Cutoff. Without clip() the G-buffer pass keeps early depth, and the
		// shadow caster drops every texture sample.
		#pragma multi_compile_local __ _VPO_ALPHATEST
		#pragma multi_compile_instancing
		#pragma instancing_options procedural:VPOSetupIndirect
		#pragma multi_compile __ UNITY_HDR_ON
		#pragma multi_compile __ UNITY_LIGHT_PROBE_PROXY_VOLUME

		#include "ValheimShaderCommon.cginc"
		#include "Assets/IndirectRendering/Shaders/VPOIndirect.cginc"

		#if defined(_VPO_TRIPLANAR_WORLD) || defined(_VPO_TRIPLANAR_LOCAL)
			#define VPO_TRIPLANAR 1
		#endif

		#define VPO_OVERRIDE_COLOR 1u
		#define VPO_OVERRIDE_EMISSION_COLOR 2u
		#define VPO_OVERRIDE_UV_OFFSET 4u
		#define VPO_OVERRIDE_TAKING_ASHLANDS_DAMAGE 8u

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
		sampler2D _EmissionMap;
		sampler2D _MetallicTex;
		sampler2D _BumpMap;

		float4 _MainTex_ST;
		float4 _Color;
		float4 _EmissionColor;
		float4 _MetalColor;
		float _Cutoff;
		float _Metallic;
		float _Glossiness;
		float _MetallicAlphaGloss;
		float _BumpScale;
		float _TriplanarScale;
		float4 _UVOffset;
		float _TakingAshlandsDamage;
		float _ValueNoise;
		float _RippleDistance;
		float _RippleFreq;
		float _MoveableObject;
		float _Wet;

		struct Input
		{
			// Mesh UV with _MainTex_ST and the UV offset applied.
			float2 vpoUV;
			float3 worldPos;
			float3 worldNormal;
			#if defined(_VPO_TRIPLANAR_LOCAL)
				// Scaled local position and normal.
				float3 triplanarPosition;
				float3 triplanarNormal;
			#endif
			// Vanilla's per-instance color (the material value or a MaterialMan
			// override) times the vertex color.
			float4 vpoColor;
			// rgb: per-instance emission color, a: taking Ashlands damage.
			float4 vpoEmission;
			float facing : VFACE;
			INTERNAL_DATA
		};

		void vert(inout appdata_full vertex, out Input output)
		{
			#if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
				vpoIndirectInstanceID = VPOGetIndirectInstanceID(UNITY_GET_INSTANCE_ID(vertex));
			#endif
			UNITY_SETUP_INSTANCE_ID(vertex);
			UNITY_INITIALIZE_OUTPUT(Input, output);
			float3 worldPosition = mul(unity_ObjectToWorld, vertex.vertex).xyz;
			float randomPhase = ValheimHash21(worldPosition.xz + worldPosition.zy);
			[branch]
			if (_RippleDistance != 0.0)
			{
				float3 displacement = ValheimRipple(worldPosition + randomPhase, _RippleFreq, _RippleDistance);
				vertex.vertex.xyz += mul((float3x3)unity_WorldToObject, displacement);
			}

			#if defined(_VPO_TRIPLANAR_LOCAL)
				// Vanilla scales local positions by the object scale so local
				// triplanar keeps world-space texel density.
				float3 objectScale = float3(
					length(unity_ObjectToWorld._m00_m10_m20),
					length(unity_ObjectToWorld._m01_m11_m21),
					length(unity_ObjectToWorld._m02_m12_m22));
				output.triplanarPosition = vertex.vertex.xyz * objectScale;
				output.triplanarNormal = mul((float3x3)unity_WorldToObject, UnityObjectToWorldNormal(vertex.normal));
			#endif

			float4 vertexColor = vertex.color;
			if (_VALUENOISEVERTEX_ON)
			{
				vertexColor.rgb *= lerp(1.0 - 0.5 * _ValueNoise, 1.0 + 0.5 * _ValueNoise, randomPhase);
			}

			float4 color = _Color;
			float3 emissionColor = _EmissionColor.rgb;
			float2 uvOffset = _UVOffset.xy;
			float takingAshlandsDamage = _TakingAshlandsDamage;
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
					if ((instanceOverride.flags & VPO_OVERRIDE_TAKING_ASHLANDS_DAMAGE) != 0)
					{
						takingAshlandsDamage = instanceOverride.takingAshlandsDamage;
					}
				}
			#endif
			// Instance values are constant across the triangle, so folding them
			// into the interpolants is exact.
			output.vpoUV = TRANSFORM_TEX(vertex.texcoord.xy, _MainTex) + uvOffset;
			output.vpoColor = color * vertexColor;
			output.vpoEmission = float4(emissionColor, takingAshlandsDamage);
		}

		void surf(Input input, inout SurfaceOutputStandard output)
		{
			#if defined(UNITY_PASS_SHADOWCASTER)
				float3 geometricNormal = normalize(input.worldNormal);
			#else
				float3 geometricNormal = normalize(WorldNormalVector(input, float3(0, 0, 1)));
			#endif
			// Samples inside dynamic branches take these explicit gradients.
			float2 uv = input.vpoUV;
			float2 uvDdx = ddx(uv);
			float2 uvDdy = ddy(uv);
			float3 positionDdx = ddx(input.worldPos);
			float3 positionDdy = ddy(input.worldPos);

			#if defined(VPO_TRIPLANAR)
				#if defined(_VPO_TRIPLANAR_LOCAL)
					float3 triplanarPosition = input.triplanarPosition;
					float3 triplanarNormal = normalize(input.triplanarNormal);
				#else
					float3 triplanarPosition = input.worldPos;
					float3 triplanarNormal = geometricNormal;
				#endif
				// Vanilla offsets the mesh UVs only; triplanar samples ignore _UVOffset.
				float2 uvX, uvY, uvZ, blend;
				ValheimPieceTriplanarCoords(triplanarPosition, triplanarNormal, _TriplanarScale, uvX, uvY, uvZ, blend);
				float4 albedo = ValheimPieceTriplanarSample(_MainTex, uvX, uvY, uvZ, blend);
				// Vanilla blends the packed texels and unpacks them as a tangent-space normal.
				float4 packedNormal = ValheimPieceTriplanarSample(_BumpMap, uvX, uvY, uvZ, blend);
			#else
				float4 albedo = tex2D(_MainTex, uv);
				float4 packedNormal = tex2D(_BumpMap, uv);
			#endif
			// Vanilla scales the normal's offset from flat, not just its xy.
			float3 normal = lerp(float3(0, 0, 1), UnpackNormal(packedNormal), _BumpScale);
			// Snow follows the bumped normal, before the two-sided flip.
			#if defined(UNITY_PASS_SHADOWCASTER)
				float bumpedUp = geometricNormal.y;
			#else
				float bumpedUp = WorldNormalVector(input, normal).y;
			#endif
			if (_TWOSIDEDNORMALS_ON)
			{
				normal.z *= input.facing >= 0.0 ? 1.0 : -1.0;
			}
			output.Normal = normal;

			// Vanilla brightens base smoothness by the untinted albedo.
			float baseSmoothness = _Glossiness * (dot(albedo.rgb, 1.0) + 1.0);
			albedo *= input.vpoColor;
			float metalMask = 0.0;
			[branch]
			if (_Metallic != 0.0)
			{
				metalMask = tex2Dgrad(_MetallicTex, uv, uvDdx, uvDdy).r * _Metallic;
			}
			// The metal tint applies to alpha too, which feeds the cutoff.
			albedo = lerp(albedo, albedo * _MetalColor, metalMask);
			float3 baseColor = albedo.rgb;
			float smoothness = lerp(baseSmoothness, _MetallicAlphaGloss, metalMask);

			// Vanilla keeps weather off moveable objects and above the clouds.
			[branch]
			if ((_ADDSNOW_ON || _ADDRAIN_ON) && _MoveableObject == 0.0 && input.worldPos.y < 2000.0)
			{
				float outdoor = ValheimOutdoorMaskGrad(input.worldPos, positionDdx.xz, positionDdy.xz, _NoiseTex);

				[branch]
				if (_ADDSNOW_ON)
				{
					float snowRegion = ValheimSnowBiomeMask(input.worldPos);
					float snow = min(snowRegion * smoothstep(0.3, 0.5, bumpedUp)
						* ValheimAshlandsSnowFade(input.worldPos), 1.0) * outdoor;
					baseColor = lerp(baseColor, 0.5, snow);
					[branch]
					if (snow > 0.0)
					{
						baseColor += ValheimSnowGlint(
							input.worldPos, geometricNormal, UnityWorldSpaceViewDir(input.worldPos)) * snowRegion * outdoor;
					}
				}

				[branch]
				if (_ADDRAIN_ON)
				{
					smoothness = lerp(smoothness, 0.7, outdoor * _Wet);
				}
			}

			// Most pieces have no emission; the color is constant per instance,
			// so the branch is coherent.
			float3 emissionColor = 0.0;
			[branch]
			if (any(input.vpoEmission.rgb != 0.0))
			{
				float4 emission = tex2Dgrad(_EmissionMap, uv, uvDdx, uvDdy);
				emissionColor = emission.rgb * emission.a * input.vpoEmission.rgb;
			}
			// Pieces burning in the Ashlands glow toward lava orange.
			[branch]
			if (input.vpoEmission.a > 0.5)
			{
				float wear = ValheimAshlandsWearGrad(input.worldPos, geometricNormal, positionDdx, positionDdy);
				emissionColor = lerp(emissionColor, float3(3.0, 0.4, 0.0), wear);
			}
			output.Emission += emissionColor;
			output.Albedo = baseColor;
			output.Metallic = metalMask;
			output.Smoothness = smoothness;
			output.Alpha = albedo.a;
			#if defined(_VPO_ALPHATEST)
				clip(output.Alpha - _Cutoff);
			#endif
		}
		ENDCG
	}
	Fallback "Standard"
}
