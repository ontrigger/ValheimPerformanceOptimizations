using Unity.Mathematics;

namespace VPOBurst
{
	public static class ConservativeProjectedBounds
	{
		private const float ClipWEpsilon = 1e-5f;

		public static bool IsLargeEnough(
		float3 center,
		float3 extents,
		float4x4 viewProjection,
		float minimumScreenPercentage)
	{
		if (minimumScreenPercentage <= 0.0f)
		{
			return true;
		}

		var clipCenter = math.mul(viewProjection, new float4(center, 1.0f));
		var clipRadius = GetClipRadius(viewProjection, extents);
		if (!math.all(math.isfinite(clipCenter)) || !math.all(math.isfinite(clipRadius)))
		{
			return true;
		}

		var minimumW = clipCenter.w - clipRadius.w;
		if (!(minimumW > ClipWEpsilon))
		{
			return true;
		}

		var maximumW = clipCenter.w + clipRadius.w;
		var projectedX = ProjectInterval(clipCenter.x, clipRadius.x, minimumW, maximumW);
		var projectedY = ProjectInterval(clipCenter.y, clipRadius.y, minimumW, maximumW);
		if (!math.all(math.isfinite(projectedX)) || !math.all(math.isfinite(projectedY)))
		{
			return true;
		}

		var minimum = new float2(projectedX.x, projectedY.x) * 0.5f + 0.5f;
		var maximum = new float2(projectedX.y, projectedY.y) * 0.5f + 0.5f;
		var screenSize = math.max(
			0.0f,
			math.min(maximum, new float2(1.0f)) - math.max(minimum, new float2(0.0f)));
			return math.max(screenSize.x, screenSize.y) >= minimumScreenPercentage;
		}

		private static float4 GetClipRadius(float4x4 viewProjection, float3 extents)
	{
		return new float4(
			math.dot(math.abs(new float3(
				viewProjection.c0.x,
				viewProjection.c1.x,
				viewProjection.c2.x)), extents),
			math.dot(math.abs(new float3(
				viewProjection.c0.y,
				viewProjection.c1.y,
				viewProjection.c2.y)), extents),
			math.dot(math.abs(new float3(
				viewProjection.c0.z,
				viewProjection.c1.z,
				viewProjection.c2.z)), extents),
			math.dot(math.abs(new float3(
				viewProjection.c0.w,
				viewProjection.c1.w,
				viewProjection.c2.w)), extents));
	}

		private static float2 ProjectInterval(
		float numeratorCenter,
		float numeratorRadius,
		float minimumW,
		float maximumW)
	{
		var minimumNumerator = numeratorCenter - numeratorRadius;
		var maximumNumerator = numeratorCenter + numeratorRadius;
		var first = minimumNumerator / minimumW;
		var second = minimumNumerator / maximumW;
		var third = maximumNumerator / minimumW;
		var fourth = maximumNumerator / maximumW;
		return new float2(
			math.min(math.min(first, second), math.min(third, fourth)),
			math.max(math.max(first, second), math.max(third, fourth)));
		}
	}
}
