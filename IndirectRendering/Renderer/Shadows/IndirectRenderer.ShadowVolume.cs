using System.Diagnostics;
using UnityEngine;
using IndirectRendering.Extensions;
using Debug = UnityEngine.Debug;

namespace IndirectRendering;

public sealed partial class IndirectRenderer
{
	private const int MaxShadowCullingPlanes = 18;
	private const float ShadowPlaneEpsilon = 0.001f;

	private readonly Plane[] shadowPlanes = new Plane[MaxShadowCullingPlanes];
	private readonly Vector4[] encodedShadowPlanes = new Vector4[MaxShadowCullingPlanes];
	private int shadowPlaneCount;

	private readonly Vector3[] nearFrustumCorners = new Vector3[4];
	private readonly Vector3[] farFrustumCorners = new Vector3[4];
	private readonly Vector3[] shadowVolumeCorners = new Vector3[8];

	private void BuildShadowCullingPlanes(Camera camera, Light directionalLight)
	{
		var shadowNear = camera.nearClipPlane;
		var shadowFar = Mathf.Min(QualitySettings.shadowDistance, camera.farClipPlane);

		shadowCascadeCount = Mathf.Clamp(QualitySettings.shadowCascades, 1, MaxDirectionalCascades);
		shadowReceiverShadowFar = shadowFar;
		camera.CalculateFrustumCorners(
			new Rect(0f, 0f, 1f, 1f),
			shadowNear,
			Camera.MonoOrStereoscopicEye.Mono,
			nearFrustumCorners);
		camera.CalculateFrustumCorners(
			new Rect(0f, 0f, 1f, 1f),
			shadowFar,
			Camera.MonoOrStereoscopicEye.Mono,
			farFrustumCorners);

		var center = Vector3.zero;
		for (var index = 0; index < 4; index += 1)
		{
			var nearCorner = camera.transform.TransformPoint(nearFrustumCorners[index]);
			var farCorner = camera.transform.TransformPoint(farFrustumCorners[index]);
			shadowVolumeCorners[index] = nearCorner;
			shadowVolumeCorners[index + 4] = farCorner;
			center += nearCorner + farCorner;
		}

		center /= shadowVolumeCorners.Length;
		var extrusionDirection = -directionalLight.transform.forward;
		Plane[] destination = shadowPlanes;
		var planeCount = 0;

		// Keep receiver-frustum faces that do not oppose the caster extrusion.
		TryAddReceiverFace(0, 1, 2, center, extrusionDirection, destination, ref planeCount);
		TryAddReceiverFace(4, 7, 6, center, extrusionDirection, destination, ref planeCount);
		TryAddReceiverFace(0, 4, 5, center, extrusionDirection, destination, ref planeCount);
		TryAddReceiverFace(3, 2, 6, center, extrusionDirection, destination, ref planeCount);
		TryAddReceiverFace(0, 3, 7, center, extrusionDirection, destination, ref planeCount);
		TryAddReceiverFace(1, 5, 6, center, extrusionDirection, destination, ref planeCount);

		// Planes through silhouette edges close the sides of the infinite extrusion.
		TryAddExtrudedEdge(0, 1, center, extrusionDirection, destination, ref planeCount);
		TryAddExtrudedEdge(1, 2, center, extrusionDirection, destination, ref planeCount);
		TryAddExtrudedEdge(2, 3, center, extrusionDirection, destination, ref planeCount);
		TryAddExtrudedEdge(3, 0, center, extrusionDirection, destination, ref planeCount);
		TryAddExtrudedEdge(4, 5, center, extrusionDirection, destination, ref planeCount);
		TryAddExtrudedEdge(5, 6, center, extrusionDirection, destination, ref planeCount);
		TryAddExtrudedEdge(6, 7, center, extrusionDirection, destination, ref planeCount);
		TryAddExtrudedEdge(7, 4, center, extrusionDirection, destination, ref planeCount);
		TryAddExtrudedEdge(0, 4, center, extrusionDirection, destination, ref planeCount);
		TryAddExtrudedEdge(1, 5, center, extrusionDirection, destination, ref planeCount);
		TryAddExtrudedEdge(2, 6, center, extrusionDirection, destination, ref planeCount);
		TryAddExtrudedEdge(3, 7, center, extrusionDirection, destination, ref planeCount);
		ValidateShadowPlanes(destination, planeCount);
		destination.EncodeTo(encodedShadowPlanes, planeCount);
		shadowPlaneCount = planeCount;
	}

	private void TryAddReceiverFace(
		int first,
		int second,
		int third,
		Vector3 center,
		Vector3 extrusionDirection,
		Plane[] destination,
		ref int planeCount)
	{
		if (!TryCreateInwardPlane(
			    shadowVolumeCorners[first],
			    shadowVolumeCorners[second],
			    shadowVolumeCorners[third],
			    center,
			    out var plane) ||
		    Vector3.Dot(plane.normal, extrusionDirection) < ShadowPlaneEpsilon)
		{
			return;
		}

		destination[planeCount] = plane;
		planeCount += 1;
	}

	private void TryAddExtrudedEdge(
		int first,
		int second,
		Vector3 center,
		Vector3 extrusionDirection,
		Plane[] destination,
		ref int planeCount)
	{
		var firstCorner = shadowVolumeCorners[first];
		var normal = Vector3.Cross(shadowVolumeCorners[second] - firstCorner, extrusionDirection);
		if (normal.sqrMagnitude < ShadowPlaneEpsilon * ShadowPlaneEpsilon)
		{
			return;
		}

		normal.Normalize();
		var plane = new Plane(normal, firstCorner);
		if (plane.GetDistanceToPoint(center) < 0f)
		{
			plane = new Plane(-normal, firstCorner);
		}

		for (var index = 0; index < shadowVolumeCorners.Length; index += 1)
		{
			if (plane.GetDistanceToPoint(shadowVolumeCorners[index]) < -ShadowPlaneEpsilon)
			{
				return;
			}
		}

		destination[planeCount] = plane;
		planeCount += 1;
	}

	[Conditional("DEBUG")]
	private static void ValidateShadowPlanes(Plane[] planes, int planeCount)
	{
		Debug.Assert(planeCount <= MaxShadowCullingPlanes, "Directional shadow plane count exceeded its capacity.");
		for (var planeIndex = 0; planeIndex < planeCount; planeIndex += 1)
		{
			var normal = planes[planeIndex].normal;
			Debug.Assert(
				!float.IsNaN(normal.x) && !float.IsInfinity(normal.x) &&
				!float.IsNaN(normal.y) && !float.IsInfinity(normal.y) &&
				!float.IsNaN(normal.z) && !float.IsInfinity(normal.z) &&
				!float.IsNaN(planes[planeIndex].distance) &&
				!float.IsInfinity(planes[planeIndex].distance),
				"Directional shadow volume produced a non-finite plane.");
		}
	}

	private static bool TryCreateInwardPlane(
		Vector3 first,
		Vector3 second,
		Vector3 third,
		Vector3 center,
		out Plane plane)
	{
		var normal = Vector3.Cross(second - first, third - first);
		if (normal.sqrMagnitude < ShadowPlaneEpsilon * ShadowPlaneEpsilon)
		{
			plane = default;
			return false;
		}

		normal.Normalize();
		plane = new Plane(normal, first);
		if (plane.GetDistanceToPoint(center) < 0f)
		{
			plane = new Plane(-normal, first);
		}

		return true;
	}
}
