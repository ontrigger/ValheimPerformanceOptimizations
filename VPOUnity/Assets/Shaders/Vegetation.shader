Shader "VPO/Vegetation"
{
	Properties
	{
		_MainTex ("Albedo (RGB)", 2D) = "white" {}
		_Color ("Color", Color) = (1,1,1,1)
		_Glossiness ("Smoothness", Range(0,1)) = 0.5
		_Metallic ("Metallic", Range(0,1)) = 0
		_Cutoff ("Alpha Cutoff", Range(0,1)) = 0.5
		[Toggle] _USEMETALMAP ("Use Metalness Map", Float) = 0
		_MetalTex ("Metal Map", 2D) = "black" {}
		_MetalGloss ("Metal Smoothness", Range(0,1)) = 0.5
		_BumpMap ("Normal Map", 2D) = "bump" {}
		_BumpScale ("Normal scale", Float) = 1
		[MaterialToggle] _TwoSidedNormals ("Twosided normals", Float) = 0
		_SphereNormals ("Spherical Normal", Range(0,1)) = 0
		_SphereOffset ("Spherical offset", Float) = 0
		_EmissiveTex ("Emissive (RGB)", 2D) = "white" {}
		[HDR] _EmissionColor ("Emissive", Color) = (0,0,0,0)
		_MossTex ("Moss (RGB)", 2D) = "white" {}
		_MossAlpha ("Moss alpha", Range(0,1)) = 0
		_MossBlend ("Moss texture blend", Range(0,10)) = 0
		_MossNormal ("MossNormal", Range(0,1)) = 0.5
		_MossTransition ("MossTransition", Float) = 0.1
		[MaterialToggle] _AddSnow ("Add Snow", Float) = 1
		[MaterialToggle] _SnowSparkleLum ("Snow Sparkle via Luminosity", Float) = 0
		[MaterialToggle] _AddRain ("Add Rain", Float) = 1
		[MaterialToggle] _UV2Height ("Use UV2 as Height", Float) = 0
		_Height ("Height", Float) = 15
		_SwaySpeed ("SwaySpeed", Float) = 15
		_SwayDistance ("SwayDistance", Float) = 0.5
		_RippleSpeed ("Ripple speed", Float) = 100
		_RippleDistance ("Ripple distance", Float) = 0.5
		_RippleDeadzoneMin ("Ripple deadzone min", Range(0,10)) = 0.3
		_RippleDeadzoneMax ("Ripple deadzone max", Range(0,10)) = 2
		_PushDistance ("Push distance", Float) = 0
		[MaterialToggle] _PushClothMode ("Push cloth mode", Float) = 0
		[MaterialToggle] _CamCull ("Near camera cull", Float) = 1
		[Enum(Off,0,Back,2)] _Cull ("Cull", Float) = 0
		// _SkyAlphaTexture and _SnowGlintOffsetNoise are globals. Declaring them
		// here makes materials bind the property default over the global.
	}

	SubShader
	{
		// Vanilla draws these in the Geometry queue. Clipped leaves go after
		// opaque geometry so its depth rejects them.
		Tags { "RenderType"="Opaque" "Queue"="AlphaTest" "DisableBatching"="True" }
		LOD 200
		Cull [_Cull]

		CGPROGRAM
		#pragma target 4.5
		#pragma surface surf Standard vertex:vert fullforwardshadows addshadow alphatest:_Cutoff dithercrossfade nometa
		// PieceMaterialReplacer leaves metal-map materials to vanilla, so the bundle
		// (which has no materials) may strip these variants.
		#pragma shader_feature_local _USEMETALMAP_ON
		// Never read here. PieceMaterialReplacer sets it so that the sky depth copy
		// of the material alpha-tests, as vanilla's DepthWrite always does.
		#pragma dynamic_branch_local _VPO_ALPHATEST
		#pragma multi_compile_instancing
		#pragma instancing_options procedural:VPOSetupIndirect
		#pragma multi_compile __ UNITY_HDR_ON
		#pragma multi_compile __ UNITY_LIGHT_PROBE_PROXY_VOLUME

		#include "ValheimShaderCommon.cginc"
		#include "Assets/IndirectRendering/Shaders/VPOIndirect.cginc"

		sampler2D _MainTex;
		sampler2D _BumpMap;
		#if defined(_USEMETALMAP_ON)
			sampler2D _MetalTex;
		#endif
		sampler2D _MossTex;
		sampler2D _EmissiveTex;

		// Declared in every variant: Unity compiles the vertex stage without the
		// fragment's keywords, so a conditional uniform would shift $Globals in
		// the fragment stage only, and the two stages would disagree on offsets.
		float _MetalGloss;
		float _BumpScale;
		float _Height;
		float _SwaySpeed;
		float _SwayDistance;
		float _RippleSpeed;
		float _RippleDistance;
		float _RippleDeadzoneMin;
		float _RippleDeadzoneMax;
		float _PushDistance;
		// Set by ClutterSystem: the local player's feet, and a position trailing
		// behind them.
		float3 _PlayerPosition;
		float3 _PlayerOldPosition;
		float _MossAlpha;
		float _MossBlend;
		float _MossNormal;
		float _MossTransition;
		int _UV2Height;
		float _Glossiness;
		float _Metallic;
		float4 _Color;
		float4 _EmissionColor;
		float _TwoSidedNormals;
		float _CamCull;
		float _SphereNormals;
		float _SphereOffset;
		float _AddSnow;
		int _SnowSparkleLum;
		float _AddRain;
		float _PushClothMode;
		// EnvMan's global wetness.
		float _Wet;
		// EnvMan blends between two winds; xyz is the direction, w the intensity.
		float4 _GlobalWind1;
		float4 _GlobalWind2;
		float _GlobalWindAlpha;

		struct Input
		{
			float2 uv_MainTex;
			float2 uv_MossTex;
			float3 worldPos;
			float3 viewDir;
			float3 worldNormal;
			// Vanilla declares this but never writes it; it stays zero.
			float2 neverWritten;
			float3 viewPosition;
			float3 objectPosition;
			float facing : VFACE;
			INTERNAL_DATA
		};

		// One wind's sway (a slow lean of the whole plant, phased by its position so
		// neighbors move out of step) and ripple (fast flutter that fades in over the
		// deadzone around the pivot, so stems stay planted).
		void VegetationWind(float4 wind, float3 position, float height, out float3 sway, out float3 ripple)
		{
			float2 speed = (wind.w * 0.5 + 0.5) * float2(_SwaySpeed, _RippleSpeed);
			float3 origin = unity_ObjectToWorld._m03_m13_m23;
			float swayX = sin(_Time.x * speed.x + origin.x);
			float swayZ = sin(speed.x * _Time.x * 0.61234 + origin.z);
			float3 phase = position * 10.0;
			float crossWave = sin(speed.y * _Time.x * 0.61234 + phase.x + phase.y);
			ripple = float3(
				crossWave * sin(_Time.x * speed.y + phase.x + phase.y),
				sin(_Time.x * speed.y + phase.z + phase.x),
				crossWave * sin(_Time.x * speed.y + phase.z + phase.y)) * _RippleDistance;
			float deadzone = saturate((length(position.xz) - _RippleDeadzoneMin) / (_RippleDeadzoneMax - _RippleDeadzoneMin));
			ripple = ripple * deadzone * wind.w * float3(0.1, 0.05, 0.1);
			// Only UV2 heights scale the ripple; mesh heights leave it whole.
			ripple = _UV2Height ? ripple * height : ripple;

			float3 direction = mul((float3x3)unity_WorldToObject, wind.xyz);
			float heightSquared = height * height;
			sway = swayX * direction * swayZ * _SwayDistance * heightSquared * wind.w
				+ direction * _SwayDistance * heightSquared * wind.w * 0.5;
		}

		// Distance from a point to the segment between the player and their
		// trailing position.
		float VegetationPushDistance3D(float3 position, float3 start, float3 end)
		{
			float endDistance = distance(end, position);
			float3 toPosition = normalize(position - start);
			float startDistance = distance(start, position);
			float segmentLength = distance(start, end);
			float3 segment = normalize(end - start);
			float3 across = normalize(cross(segment, cross(segment, toPosition)));
			float along = dot(segment, position - start);
			float acrossDistance = abs(dot(across, position - start));
			float distanceToSegment = segmentLength < along ? endDistance : acrossDistance;
			return segmentLength < 0.01 || along < 0.0 ? startDistance : distanceToSegment;
		}

		float VegetationPushDistance2D(float2 position, float2 start, float2 end)
		{
			float endDistance = distance(end, position);
			float2 segment = normalize(end - start);
			float2 across = normalize(float2(segment.y, -segment.x));
			float along = dot(segment, position - start);
			float acrossDistance = abs(dot(across, position - start));
			float segmentLength = distance(start, end);
			float distanceToSegment = segmentLength < along ? endDistance : acrossDistance;
			return along < 0.0 || segmentLength < 0.01 ? distance(start, position) : distanceToSegment;
		}

		void vert(inout appdata_full vertex, out Input output)
		{
			#if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
				vpoIndirectInstanceID = VPOGetIndirectInstanceID(UNITY_GET_INSTANCE_ID(vertex));
			#endif
			UNITY_SETUP_INSTANCE_ID(vertex);
			UNITY_INITIALIZE_OUTPUT(Input, output);
			float height = saturate(vertex.vertex.y / _Height);
			height = _UV2Height ? saturate(vertex.texcoord1.y) : height;
			float3 sway1, ripple1, sway2, ripple2;
			VegetationWind(_GlobalWind1, vertex.vertex.xyz, height, sway1, ripple1);
			VegetationWind(_GlobalWind2, vertex.vertex.xyz, height, sway2, ripple2);
			float3 ripple = lerp(ripple1, ripple2, _GlobalWindAlpha);
			float3 sway = lerp(sway1, sway2, _GlobalWindAlpha);
			// Sway bends around the pivot instead of stretching the mesh.
			float4 position = float4(normalize(vertex.vertex.xyz + sway) * length(vertex.vertex.xyz) + ripple, vertex.vertex.w);

			// The player pushes the plant away along the path they just walked,
			// measured 1 m above their feet in cloth mode.
			float4 worldPosition = mul(unity_ObjectToWorld, position);
			float pushDistance = _PushClothMode > 0.5
				? VegetationPushDistance3D(
					worldPosition.xyz,
					_PlayerPosition + float3(0.0, 1.0, 0.0),
					_PlayerOldPosition + float3(0.0, 1.0, 0.0))
				: VegetationPushDistance2D(worldPosition.xz, _PlayerPosition.xz, _PlayerOldPosition.xz);
			if (!(1.0 < pushDistance || worldPosition.y < _PlayerPosition.y))
			{
				float3 pushDirection = float3(0.0, 0.0, 0.0);
				pushDirection.xz = normalize(unity_ObjectToWorld._m03_m23 - _PlayerPosition.xz);
				float push = 1.0 - min(pushDistance, 1.0);
				// Cloth bulges outward; plants also bend down toward the ground.
				float3 clothPosition = worldPosition.xyz + pushDirection * pow(push, 0.5) * _PushDistance;
				float plantPush = height * push;
				float3 plantPosition = worldPosition.xyz + pushDirection * plantPush * _PushDistance;
				plantPosition.y -= plantPush * _PushDistance;
				float3 pushedPosition = _PushClothMode > 0.5 ? clothPosition : plantPosition;
				// Back to object space, keeping the vertex's distance from the pivot.
				float scale = length(position);
				position = normalize(mul(unity_WorldToObject, float4(pushedPosition, worldPosition.w))) * scale;
			}
			vertex.vertex = position;

			float3 sphereNormal = normalize(position.xyz - float3(0.0, _SphereOffset, 0.0));
			vertex.normal = lerp(vertex.normal, sphereNormal, _SphereNormals);

			output.viewPosition = UnityObjectToViewPos(position.xyz);
			output.objectPosition = unity_ObjectToWorld._m03_m13_m23;
		}

		// Snow glints: a world-space cell grid that coarsens in 1.3x distance bands,
		// shifted by time and view so sparkles twinkle as the camera moves.
		float VegetationSnowGlint(float3 worldPosition, float3 viewDirection)
		{
			float3 view = normalize(viewDirection);
			float cameraDistance = distance(worldPosition, _WorldSpaceCameraPos);
			float cellDensity = _SnowGlintDensity * 15.0;
			float threshold = 1.0 - _SnowGlintDensity * _SnowGlintDensity * _SnowGlintSize * 0.2;
			float band = pow(1.3, floor(log(cameraDistance / _ProjectionParams.z * 0.3 + 3.0) / log(1.3)));
			float scale = band * 0.1 - 0.2;
			float frequency = 0.12 / scale;
			float cellFrequency = cellDensity * frequency;
			float noiseFrequency = frequency * _SnowGlintNoiseDensity;
			float3 viewOffset = scale * view;
			float3 viewSign = sign(viewOffset);
			viewOffset = fmod(abs(viewOffset), 1.0) * viewSign;
			float timeOffset = abs(fmod(_SnowGlintTimeAmount * _Time.x, 1.0) - 0.5) * 0.2;
			float3 cellPosition = cellFrequency * worldPosition + timeOffset;
			cellPosition = _SnowGlintViewAmount * normalize(viewOffset) + cellPosition;
			float3 cell = floor(cellPosition);
			float3 local = cellPosition - cell;
			float3 jitter = tex3Dlod(_SnowGlintOffsetNoise, float4(cell * (1.0 / cellFrequency) * noiseFrequency, 0.0)).xyz;
			jitter = fmod(_SnowGlintNoiseAmount * jitter + 0.5, 1.0);
			local = jitter * 0.5 + local - 0.75;
			float distanceSquared = dot(local, local);
			float radiusSquared = 1.0 - threshold;
			float glint = distanceSquared < radiusSquared ? (1.0 - distanceSquared - threshold) / radiusSquared * 20.0 : 0.0;
			return saturate(glint) * _SnowGlintStrength;
		}

		void surf(Input input, inout SurfaceOutputStandard output)
		{
			// Shadows keep the full plant; only the camera's own view is cleared.
			#if !defined(UNITY_PASS_SHADOWCASTER)
				if (_CamCull > 0.5 && length(input.viewPosition) < 2.0)
				{
					discard;
				}
			#endif

			// Vanilla scales the normal's offset from flat, not just its xy.
			float3 normal = lerp(float3(0, 0, 1), UnpackNormal(tex2D(_BumpMap, input.uv_MainTex)), _BumpScale);
			if (_TwoSidedNormals > 0.5)
			{
				normal = input.facing > 0.0 ? normal : -normal;
			}
			float4 albedo = tex2D(_MainTex, input.uv_MainTex);
			float3 baseColor = albedo.rgb * _Color.rgb;
			// Vanilla brightens smoothness by the untinted albedo.
			float smoothness = _Glossiness * dot(1.0, albedo.rgb) + _Glossiness;
			#if defined(_USEMETALMAP_ON)
				float metalMask = tex2D(_MetalTex, input.uv_MainTex).r * _Metallic;
				smoothness = lerp(smoothness, _MetalGloss, metalMask);
			#endif
			// Moss and snow follow the bumped normal, after the two-sided flip.
			float bumpedUp = WorldNormalVector(input, normal).y;

			// Moss gathers in dark texels (_MossBlend) or everywhere (_MossAlpha) on
			// faces whose bumped normal points above _MossNormal.
			float4 moss = tex2D(_MossTex, input.uv_MossTex);
			float mossTexture = saturate((1.0 - pow(dot(1.0, albedo.rgb), 0.25)) * _MossBlend);
			float mossSlope = smoothstep(0.0, _MossTransition, bumpedUp - _MossNormal);
			float mossMask = max(mossTexture, _MossAlpha) * mossSlope;
			mossMask *= mossMask;
			baseColor = lerp(baseColor, moss.rgb, mossMask);
			smoothness = lerp(smoothness, 0.0, mossMask);

			float4 emissive = tex2D(_EmissiveTex, input.uv_MainTex);
			float3 emission = emissive.rgb * emissive.a * _EmissionColor.rgb;

			// Weather stays off above the clouds.
			[branch]
			if (input.worldPos.y < 2000.0)
			{
				float outdoor = ValheimOutdoorMask(input.worldPos, _NoiseTex);
				[branch]
				if (_AddSnow > 0.5)
				{
					bool deepNorth = ValheimDeepNorthMask(input.worldPos);
					// Mountains test the object's height, so a plant is all snow or none.
					float mountain = saturate((input.objectPosition.y - 85.0) / 15.0) * ValheimAshlandsSnowFade(input.worldPos);
					float biome = deepNorth ? 1.0 : mountain;
					// Vanilla tests the tangent-space normal here, unlike moss.
					float snow = biome * smoothstep(-0.25, 0.25, normal.y) * outdoor;
					baseColor = lerp(baseColor, 0.5, snow);
				}
				if (_AddRain > 0.5)
				{
					smoothness = lerp(smoothness, 0.8, outdoor * _Wet);
				}
			}

			float luminance = dot(float3(0.299, 0.587, 0.114), baseColor);
			float sparkle = _SnowSparkleLum ? luminance * luminance : 0.0;
			[branch]
			if (sparkle > 0.0)
			{
				baseColor += sparkle * VegetationSnowGlint(input.worldPos, input.viewDir) * 0.5;
			}

			output.Normal = normal;
			output.Albedo = baseColor;
			output.Emission = emission;
			// The metal map only feeds smoothness; metalness stays uniform.
			output.Metallic = _Metallic;
			output.Smoothness = smoothness;
			output.Alpha = albedo.a * _Color.a;
		}
		ENDCG
	}
	Fallback "Diffuse"
}
