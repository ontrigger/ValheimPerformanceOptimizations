using UnityEngine;
using IndirectRendering.Extensions;

namespace IndirectRendering;

/// <summary>
/// The planes around every directional shadow caster that can shadow the
/// camera's view within the shadow distance: the camera frustum, cut at the
/// shadow distance and extruded away from the light.
/// </summary>
internal sealed class DirectionalShadowVolume
{
	internal const int MaxPlanes = 18;
	private const float PlaneEpsilon = 0.001f;

	private readonly Vector3[] nearFrustumCorners = new Vector3[4];
	private readonly Vector3[] farFrustumCorners = new Vector3[4];
	private readonly Vector3[] corners = new Vector3[8];

	internal Plane[] Planes { get; } = new Plane[MaxPlanes];
	// Planes as (normal, distance), the layout the shadow cull reads.
	internal Vector4[] EncodedPlanes { get; } = new Vector4[MaxPlanes];
	internal int PlaneCount { get; private set; }
	// The distance the volume was cut at.
	internal float ShadowFar { get; private set; }

	internal void Build(Camera camera, Light directionalLight)
	{
		var shadowNear = camera.nearClipPlane;
		var shadowFar = Mathf.Min(QualitySettings.shadowDistance, camera.farClipPlane);

		ShadowFar = shadowFar;
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
			corners[index] = nearCorner;
			corners[index + 4] = farCorner;
			center += nearCorner + farCorner;
		}

		center /= corners.Length;
		var extrusionDirection = -directionalLight.transform.forward;
		var planeCount = 0;

		// Keep receiver-frustum faces that do not oppose the caster extrusion.
		TryAddReceiverFace(0, 1, 2, center, extrusionDirection, ref planeCount);
		TryAddReceiverFace(4, 7, 6, center, extrusionDirection, ref planeCount);
		TryAddReceiverFace(0, 4, 5, center, extrusionDirection, ref planeCount);
		TryAddReceiverFace(3, 2, 6, center, extrusionDirection, ref planeCount);
		TryAddReceiverFace(0, 3, 7, center, extrusionDirection, ref planeCount);
		TryAddReceiverFace(1, 5, 6, center, extrusionDirection, ref planeCount);

		// Planes through silhouette edges close the sides of the infinite extrusion.
		TryAddExtrudedEdge(0, 1, center, extrusionDirection, ref planeCount);
		TryAddExtrudedEdge(1, 2, center, extrusionDirection, ref planeCount);
		TryAddExtrudedEdge(2, 3, center, extrusionDirection, ref planeCount);
		TryAddExtrudedEdge(3, 0, center, extrusionDirection, ref planeCount);
		TryAddExtrudedEdge(4, 5, center, extrusionDirection, ref planeCount);
		TryAddExtrudedEdge(5, 6, center, extrusionDirection, ref planeCount);
		TryAddExtrudedEdge(6, 7, center, extrusionDirection, ref planeCount);
		TryAddExtrudedEdge(7, 4, center, extrusionDirection, ref planeCount);
		TryAddExtrudedEdge(0, 4, center, extrusionDirection, ref planeCount);
		TryAddExtrudedEdge(1, 5, center, extrusionDirection, ref planeCount);
		TryAddExtrudedEdge(2, 6, center, extrusionDirection, ref planeCount);
		TryAddExtrudedEdge(3, 7, center, extrusionDirection, ref planeCount);
		Planes.EncodeTo(EncodedPlanes, planeCount);
		PlaneCount = planeCount;
	}

	private void TryAddReceiverFace(
		int first,
		int second,
		int third,
		Vector3 center,
		Vector3 extrusionDirection,
		ref int planeCount)
	{
		if (!TryCreateInwardPlane(
			    corners[first],
			    corners[second],
			    corners[third],
			    center,
			    out var plane) ||
		    Vector3.Dot(plane.normal, extrusionDirection) < PlaneEpsilon)
		{
			return;
		}

		Planes[planeCount] = plane;
		planeCount += 1;
	}

	private void TryAddExtrudedEdge(
		int first,
		int second,
		Vector3 center,
		Vector3 extrusionDirection,
		ref int planeCount)
	{
		var firstCorner = corners[first];
		var normal = Vector3.Cross(corners[second] - firstCorner, extrusionDirection);
		if (normal.sqrMagnitude < PlaneEpsilon * PlaneEpsilon)
		{
			return;
		}

		normal.Normalize();
		var plane = new Plane(normal, firstCorner);
		if (plane.GetDistanceToPoint(center) < 0f)
		{
			plane = new Plane(-normal, firstCorner);
		}

		for (var index = 0; index < corners.Length; index += 1)
		{
			if (plane.GetDistanceToPoint(corners[index]) < -PlaneEpsilon)
			{
				return;
			}
		}

		Planes[planeCount] = plane;
		planeCount += 1;
	}

	private static bool TryCreateInwardPlane(
		Vector3 first,
		Vector3 second,
		Vector3 third,
		Vector3 center,
		out Plane plane)
	{
		var normal = Vector3.Cross(second - first, third - first);
		if (normal.sqrMagnitude < PlaneEpsilon * PlaneEpsilon)
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
