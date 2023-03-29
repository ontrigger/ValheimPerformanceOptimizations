// Upgrade NOTE: upgraded instancing buffer 'LuxParticlesLocalSH' to new syntax.



#ifndef LUXPARTICLES_UTILS_INCLUDED
#define LUXPARTICLES_UTILS_INCLUDED

//	General helper Functions -------------------------------------------------
	
	inline fixed3 Lux_UnpackNormalDXT5nm (fixed2 packednormal) {
	    fixed3 normal;
	    normal.xy = packednormal.xy * 2 - 1;
	    normal.z = sqrt(1 - saturate(dot(normal.xy, normal.xy)));
	    return normal;
	}


//	Lighting Functions -------------------------------------------------

	fixed3 _AmbientColor;
	fixed _WrappedDiffuse;
	fixed _Translucency;
	half _AlphaInfluence;

	#if !defined(GEOM_TYPE_LEAF)
		float4 _Lux_SHAr;
		float4 _Lux_SHAg;
		float4 _Lux_SHAb;
		float4 _Lux_SHBr;
		float4 _Lux_SHBg;
		float4 _Lux_SHBb;
		float4 _Lux_SHC;
	#endif

	UNITY_INSTANCING_BUFFER_START (LuxParticlesLocalSH)
		#if defined(GEOM_TYPE_LEAF)
			UNITY_DEFINE_INSTANCED_PROP (float4, _Lux_L_SHAr)
#define _Lux_L_SHAr_arr LuxParticlesLocalSH
			UNITY_DEFINE_INSTANCED_PROP (float4, _Lux_L_SHAg)
#define _Lux_L_SHAg_arr LuxParticlesLocalSH
			UNITY_DEFINE_INSTANCED_PROP (float4, _Lux_L_SHAb)
#define _Lux_L_SHAb_arr LuxParticlesLocalSH
			UNITY_DEFINE_INSTANCED_PROP (float4, _Lux_L_SHBr)
#define _Lux_L_SHBr_arr LuxParticlesLocalSH
			UNITY_DEFINE_INSTANCED_PROP (float4, _Lux_L_SHBg)
#define _Lux_L_SHBg_arr LuxParticlesLocalSH
			UNITY_DEFINE_INSTANCED_PROP (float4, _Lux_L_SHBb)
#define _Lux_L_SHBb_arr LuxParticlesLocalSH
			UNITY_DEFINE_INSTANCED_PROP (float4, _Lux_L_SHC)
#define _Lux_L_SHC_arr LuxParticlesLocalSH
		#endif
	UNITY_INSTANCING_BUFFER_END(LuxParticlesLocalSH)


//	SH lighting (from sky)
	#if !defined(GEOM_TYPE_LEAF)
		half3 Lux_ShadeSH (float4 wNormal) {
			half3 x1, x2, x3;
			// Linear + constant polynomial terms
			x1.r = dot(_Lux_SHAr, wNormal);
			x1.g = dot(_Lux_SHAg, wNormal);
			x1.b = dot(_Lux_SHAb, wNormal);
			// 4 of the quadratic polynomials
			half4 vB = wNormal.xyzz * wNormal.yzzx;
			x2.r = dot(_Lux_SHBr,vB);
			x2.g = dot(_Lux_SHBg,vB);
			x2.b = dot(_Lux_SHBb,vB);
			// Final quadratic polynomial
			float vC = wNormal.x*wNormal.x - wNormal.y*wNormal.y;
			x3 = _Lux_SHC * vC;
			return (x1 + x2 + x3);
		}
	#else

		half3 Lux_ShadeSH (float4 wNormal) {
			half3 x1, x2, x3;
			// Linear + constant polynomial terms
			x1.r = dot(UNITY_ACCESS_INSTANCED_PROP(LuxParticlesLocalSH, _Lux_L_SHAr), wNormal);
			x1.g = dot(UNITY_ACCESS_INSTANCED_PROP(LuxParticlesLocalSH, _Lux_L_SHAg), wNormal);
			x1.b = dot(UNITY_ACCESS_INSTANCED_PROP(LuxParticlesLocalSH, _Lux_L_SHAb), wNormal);
			// 4 of the quadratic polynomials
			half4 vB = wNormal.xyzz * wNormal.yzzx;
			x2.r = dot(UNITY_ACCESS_INSTANCED_PROP(LuxParticlesLocalSH, _Lux_L_SHBr),vB);
			x2.g = dot(UNITY_ACCESS_INSTANCED_PROP(LuxParticlesLocalSH, _Lux_L_SHBg),vB);
			x2.b = dot(UNITY_ACCESS_INSTANCED_PROP(LuxParticlesLocalSH, _Lux_L_SHBb),vB);
			// Final quadratic polynomial
			float vC = wNormal.x*wNormal.x - wNormal.y*wNormal.y;
			x3 = UNITY_ACCESS_INSTANCED_PROP(LuxParticlesLocalSH, _Lux_L_SHC) * vC;
			return (x1 + x2 + x3);
		}
	#endif


	inline float DistanceAtten(float lengthSq, float4 atten) {
		// This actually gives me too less attenuation.
		// return 1.0 / (1.0 + lengthSq * atten.z);
		// So we also take the squared light range into account.
		return saturate ( 1.0 / (1.0 + (lengthSq * atten.z)) * (1.0 - (lengthSq / atten.w)) );
	}

	inline float SpotAtten(float3 toLight, float i) {
		if(unity_LightAtten[i].x == -1.0f) {
			return 1.0f;
		}
		float rho = max (0.0, dot(toLight, unity_SpotDirection[i].xyz));
		float atten = saturate(rho - unity_LightAtten[i].x) * unity_LightAtten[i].y;
		return atten;
	}
			
//	Calculate LightDir and Attenuation for the fragment shader. See: float3 ShadeVertexLightsFull from UniytCG
	inline float4 LightDirAndAtten( float3 viewpos, float index)
	{
		float3 toLight = unity_LightPosition[index].xyz - viewpos.xyz * unity_LightPosition[index].w;
		float lengthSq = dot(toLight, toLight);
		// don't produce NaNs if some vertex position overlaps with the light
		lengthSq = max(lengthSq, 0.000001);
		toLight *= rsqrt(lengthSq);
		// Calculate Distance based Attenuation
		float atten = DistanceAtten(lengthSq, unity_LightAtten[index]);
		#if !defined(GEOM_TYPE_BRANCH)
			// Add Spot Attenuation per Vertex
			return float4(toLight, atten * SpotAtten(toLight, index) );
		#else
			// Spot Attenuation will be added per Pixel
			return float4(toLight, atten);
		#endif
	}

//	Per Vertex Lighting function, see: float3 ShadeVertexLightsFull from UniytCG
	inline half3 ApplyVertexLight(float3 viewN, float3 viewpos, float index )
	{
		float3 toLight = unity_LightPosition[index].xyz - viewpos.xyz * unity_LightPosition[index].w;
		float lengthSq = dot(toLight, toLight);
		// don't produce NaNs if some vertex position overlaps with the light
		lengthSq = max(lengthSq, 0.000001);
		toLight *= rsqrt(lengthSq);
		// Light Attenuation
		float atten = DistanceAtten(lengthSq, unity_LightAtten[index] + 1) * SpotAtten(toLight, index);
		float NdotL = dot(toLight, viewN);
		// Wrapped around diffuse Lighting
		float light = saturate(NdotL * (1.0 - _WrappedDiffuse) + _WrappedDiffuse);
		// Backlighting
		light += saturate(dot(toLight.xyz, -viewN) * _Translucency);
		half3 lighting = unity_LightColor[index] * light * atten;
		return lighting;
	}

//	Per Pixel Lighting functions
	inline fixed3 ApplyDirectionalPixelLight (fixed3 albedo, float4 LightDirAndAtten, float3 normal, float LightIndex, fixed alpha) {
		float NdotL = dot(LightDirAndAtten.xyz, normal);
		// Wrapped around diffuse Lighting
		float light = saturate(NdotL * (1.0 - _WrappedDiffuse) + _WrappedDiffuse);
		// Backlighting
		light += saturate(dot(LightDirAndAtten.xyz, -normal) * _Translucency * saturate(1.0 - alpha * _AlphaInfluence));
		light *= LightDirAndAtten.w;
		fixed3 lighting = albedo * unity_LightColor[LightIndex] * light;
		return lighting;
	}

	inline fixed3 ApplyPixelLight (fixed3 albedo, float4 LightDirAndAtten, float3 normal, float LightIndex) {
		float NdotL = dot(LightDirAndAtten.xyz, normal);
		// Wrapped around diffuse Lighting
		float light = saturate(NdotL * (1.0 - _WrappedDiffuse) + _WrappedDiffuse);
		// Backlighting
		light += saturate( dot(LightDirAndAtten.xyz, -normal) * _Translucency);
		light *= LightDirAndAtten.w;
		// Calculate Spot Attenuation per pixel
		light *= SpotAtten(LightDirAndAtten.xyz, LightIndex);
		fixed3 lighting = albedo * unity_LightColor[LightIndex] * light;
		return lighting;
	}


//	Shadow Helpers -------------------------------------------------

	UNITY_DECLARE_SHADOWMAP(_LuxParticles_CascadedShadowMap);

	float computeShadowFadeDistance(float3 wpos, float z) {
    	float sphereDist = distance(wpos, unity_ShadowFadeCenterAndType.xyz);
    	return lerp(z, sphereDist, unity_ShadowFadeCenterAndType.w);
	}
	half computeShadowFade(float fadeDist) {
	    return saturate(fadeDist * _LightShadowData.z + _LightShadowData.w);
	}

	inline fixed4 getCascadeWeights_splitSpheres(float3 wpos) {
		float3 fromCenter0 = wpos.xyz - unity_ShadowSplitSpheres[0].xyz;
		float3 fromCenter1 = wpos.xyz - unity_ShadowSplitSpheres[1].xyz;
		float3 fromCenter2 = wpos.xyz - unity_ShadowSplitSpheres[2].xyz;
		float3 fromCenter3 = wpos.xyz - unity_ShadowSplitSpheres[3].xyz;
		float4 distances2 = float4(dot(fromCenter0, fromCenter0), dot(fromCenter1, fromCenter1), dot(fromCenter2, fromCenter2), dot(fromCenter3, fromCenter3));
		fixed4 weights = float4(distances2 < unity_ShadowSplitSqRadii);
		weights.yzw = saturate(weights.yzw - weights.xyz);
		return weights;
	}

	inline float4 getShadowCoord(float4 wpos, fixed4 cascadeWeights) {
		float3 sc0 = mul(unity_WorldToShadow[0], wpos).xyz;
		float3 sc1 = mul(unity_WorldToShadow[1], wpos).xyz;
		float3 sc2 = mul(unity_WorldToShadow[2], wpos).xyz;
		float3 sc3 = mul(unity_WorldToShadow[3], wpos).xyz;
		float4 shadowMapCoordinate = float4(sc0 * cascadeWeights[0] + sc1 * cascadeWeights[1] + sc2 * cascadeWeights[2] + sc3 * cascadeWeights[3], 1);
		#if defined(UNITY_REVERSED_Z)
			float  noCascadeWeights = 1 - dot(cascadeWeights, float4(1, 1, 1, 1));
			shadowMapCoordinate.z += noCascadeWeights;
		#endif
		return shadowMapCoordinate;
	}

	float GetLightAttenuation(float3 wpos) {
	//	Directional Lights only
		float atten = 1;
		fixed4 cascadeWeights = getCascadeWeights_splitSpheres(wpos);
		float4 coord = getShadowCoord(float4(wpos, 1), cascadeWeights);
		atten = UNITY_SAMPLE_SHADOW(_LuxParticles_CascadedShadowMap, coord.xyz);
	//	Fade out shadows
	    float zDist = dot(_WorldSpaceCameraPos - wpos, UNITY_MATRIX_V[2].xyz);
	    float fadeDist = computeShadowFadeDistance(wpos, zDist);
	    half  realtimeShadowFade = computeShadowFade(fadeDist);
		atten = lerp (_LightShadowData.r, 1.0f, saturate(atten + realtimeShadowFade));
		return atten;
	}


#endif // LUXPARTICLES_UTILS_INCLUDED