#ifndef VALHEIM_SHADER_COMMON_INCLUDED
#define VALHEIM_SHADER_COMMON_INCLUDED

#include "ValheimOutdoor.cginc"

sampler3D _SnowGlintOffsetNoise;
sampler3D _AshlandsWearTexture;

float _SnowGlintSize;
float _SnowGlintDensity;
float _SnowGlintStrength;
float _SnowGlintNoiseDensity;
float _SnowGlintNoiseAmount;
float _SnowGlintViewAmount;
float _SnowGlintTimeAmount;

inline float ValheimHash21(float2 value)
{
	return frac(sin(dot(value, float2(12.9898, 78.233))) * 43758.546875);
}

inline float3 ValheimRipple(float3 worldPosition, float frequency, float distance)
{
	float3 phase = (worldPosition.yzx + worldPosition.zxy) * frequency;
	float3 wave = sin(phase) * cos(phase * float3(0.568562, 0.457431, 0.642344));
	wave.y *= 0.4;
	return wave * distance;
}

inline float3 ValheimTriplanarWeights(float3 worldNormal)
{
	float3 weights = pow(abs(worldNormal.xzy), 4.0);
	return weights / max(dot(weights, 1.0), 1e-5);
}

// Vanilla piece triplanar: Z and X projections blend by their relative weight,
// then Y is layered on top by its unnormalized weight.
// blend = (x / (x + z), y) with weights = normal^4.
inline void ValheimPieceTriplanarCoords(
	float3 position,
	float3 normal,
	float scale,
	out float2 uvX,
	out float2 uvY,
	out float2 uvZ,
	out float2 blend)
{
	float3 scaledPosition = position * scale;
	float3 signs = sign(normal);
	uvX = float2(scaledPosition.z, -signs.x * scaledPosition.y);
	uvY = float2(scaledPosition.x, -signs.y * scaledPosition.z);
	uvZ = float2(scaledPosition.x, signs.z * scaledPosition.y);
	float3 weights = normal * normal;
	weights *= weights;
	float sideWeight = weights.x + weights.z;
	blend = float2(sideWeight > 0.0 ? weights.x / sideWeight : 0.0, weights.y);
}

inline float4 ValheimPieceTriplanarSample(sampler2D source, float2 uvX, float2 uvY, float2 uvZ, float2 blend)
{
	float4 side = lerp(tex2D(source, uvZ), tex2D(source, uvX), blend.x);
	return lerp(side, tex2D(source, uvY), blend.y);
}

inline float4 ValheimTriplanarColor(
	sampler2D source,
	float3 position,
	float3 normal,
	float scale)
{
	float3 weights = ValheimTriplanarWeights(normal);
	float3 signs = normal < 0.0 ? -1.0 : 1.0;
	float4 x = tex2D(source, position.zy * float2(signs.x, 1.0) * scale);
	float4 y = tex2D(source, position.xz * float2(signs.y, 1.0) * scale);
	float4 z = tex2D(source, position.xy * float2(-signs.z, 1.0) * scale);
	return x * weights.x + y * weights.y + z * weights.z;
}

inline float3 ValheimTriplanarNormal(
	sampler2D source,
	float3 position,
	float3 normal,
	float scale,
	float strength)
{
	float3 weights = ValheimTriplanarWeights(normal);
	float3 signs = normal < 0.0 ? -1.0 : 1.0;
	float3 nx = UnpackScaleNormal(tex2D(source, position.zy * float2(signs.x, 1.0) * scale), strength).zyx;
	float3 ny = UnpackScaleNormal(tex2D(source, position.xz * float2(signs.y, 1.0) * scale), strength).xzy;
	float3 nz = UnpackScaleNormal(tex2D(source, position.xy * float2(-signs.z, 1.0) * scale), strength).xyz;
	nx.x *= signs.x;
	ny.y *= signs.y;
	nz.z *= signs.z;
	return normalize(nx * weights.x + ny * weights.y + nz * weights.z);
}

// WorldGenerator.IsDeepnorth.
inline bool ValheimDeepNorthMask(float3 worldPosition)
{
	float angle = atan2(worldPosition.x, worldPosition.z);
	float polarDistance = length(worldPosition.xz + float2(0.0, 4000.0));
	return sin(angle * 20.0) * 100.0 + 12000.0 < polarDistance;
}

// WorldGenerator.IsDeepnorth, or mountain height.
inline float ValheimSnowBiomeMask(float3 worldPosition)
{
	float deepNorth = ValheimDeepNorthMask(worldPosition) ? 1.0 : 0.0;
	float mountain = smoothstep(90.0, 110.0, worldPosition.y);
	return max(deepNorth, mountain);
}

// Fades out over 25 m into the Ashlands (WorldGenerator.IsAshlands).
inline float ValheimAshlandsSnowFade(float3 worldPosition)
{
	float2 offset = worldPosition.xz - float2(0.0, 4000.0);
	float edge = 11987.5 + sin(atan2(offset.x, offset.y) * 20.0) * 100.0;
	return 1.0 - saturate((length(offset) - edge) * 0.04);
}

inline float3 ValheimSnowGlint(float3 worldPosition, float3 worldNormal, float3 viewDirection)
{
	float3 view = normalize(viewDirection);
	float distanceScale = max(0.1, floor(log2(distance(worldPosition, _WorldSpaceCameraPos) * 0.3 + 3.0)) * 0.1);
	float cellSize = max(0.001, _SnowGlintSize * 15.0 * (0.12 / distanceScale));
	float3 cell = floor(worldPosition / cellSize);
	float3 offset = tex3Dlod(_SnowGlintOffsetNoise, float4(cell * distanceScale, 0)).xyz;
	float3 local = frac(worldPosition / cellSize) + frac(offset * _SnowGlintNoiseAmount) * 0.5 - 0.75;
	float radius = saturate((1.0 - dot(local, local) - _SnowGlintDensity) * 20.0);
	float facing = saturate((dot(worldNormal, view) - 0.3) * 5.0);
	float twinkle = 0.5 + 0.5 * sin(_Time.y * _SnowGlintTimeAmount + dot(cell, 1.0));
	return radius * facing * twinkle * _SnowGlintStrength * 0.5;
}

// Takes ddx/ddy(worldPosition) so it can run inside dynamic branches.
inline float ValheimAshlandsWearGrad(
	float3 worldPosition,
	float3 worldNormal,
	float3 positionDdx,
	float3 positionDdy)
{
	float3 p0 = worldPosition * 0.5 + positionDdx;
	float3 p1 = worldPosition * 0.5 + positionDdy + float3(0.01, -0.154, 0.01053) * _Time.y;
	float wear = tex3Dlod(_AshlandsWearTexture, float4(p0, 0)).r;
	wear *= tex3Dlod(_AshlandsWearTexture, float4(p1, 0)).g;
	return wear * saturate(worldNormal.y * 3.0 + 0.4);
}

inline float ValheimAshlandsWear(float3 worldPosition, float3 worldNormal)
{
	return ValheimAshlandsWearGrad(worldPosition, worldNormal, ddx(worldPosition), ddy(worldPosition));
}

#endif
