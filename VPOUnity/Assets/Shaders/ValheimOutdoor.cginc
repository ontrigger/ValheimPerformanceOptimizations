#ifndef VALHEIM_OUTDOOR_INCLUDED
#define VALHEIM_OUTDOOR_INCLUDED

#include "UnityCG.cginc"

sampler2D _SkyAlphaTexture;
// Bound per material.
sampler2D _NoiseTex;

// Declared before any other Valheim global, as in vanilla's $Globals layout.
float3 _SkyAlphaPosition;

// Explicit gradients (of worldPosition.xz) keep the samples valid inside
// dynamic branches, so the compiler need not flatten them.
inline float ValheimOutdoorMaskGrad(
	float3 worldPosition,
	float2 positionDdx,
	float2 positionDdy,
	sampler2D noiseTexture)
{
	float2 noise = tex2Dgrad(noiseTexture, worldPosition.xz, positionDdx, positionDdy).xy;
	float2 skyUV = (worldPosition.xz - _SkyAlphaPosition.xz) * 0.01 + ((noise - 0.5) * 0.003 + 0.5);
	float skyDepth = tex2Dgrad(_SkyAlphaTexture, skyUV, positionDdx * 0.01, positionDdy * 0.01).r;
	float heightDelta = distance(worldPosition, _SkyAlphaPosition) - skyDepth * 100.0;
	// Surfaces past the sky depth map's edge count as outdoors.
	float insideMap = 1.0 - smoothstep(0.4, 0.5, length(0.5 - skyUV));
	return heightDelta < 0.1 ? 1.0 : 1.0 - insideMap;
}

// Vanilla's form, with implicit derivatives. Inside a branch they are only
// valid where the whole quad takes it.
inline float ValheimOutdoorMask(float3 worldPosition, sampler2D noiseTexture)
{
	float2 noise = tex2D(noiseTexture, worldPosition.xz).xy;
	float2 skyUV = (noise - 0.5) * 0.003 + 0.5;
	skyUV = (worldPosition.xz - _SkyAlphaPosition.xz) * 0.01 + skyUV;
	float skyDepth = tex2D(_SkyAlphaTexture, skyUV).r;
	float skyDistance = distance(worldPosition, _SkyAlphaPosition);
	float insideMap = 1.0 - smoothstep(0.4, 0.5, length(0.5 - skyUV));
	return skyDistance - skyDepth * 100.0 < 0.1 ? 1.0 : 1.0 - insideMap;
}

#endif
