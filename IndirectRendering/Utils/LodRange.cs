using System;
using UnityEngine;

namespace IndirectRendering;

/// <summary>
/// Packs an instance's LOD test the way the cull kernels read it: the world
/// reference point in xyz and the start and end camera distances as halves
/// in w. See IsInLodRange in PieceFrustumCull.compute.
/// </summary>
public static class LodRange
{
	private const float MaximumHalf = 65504f;

	/// <summary>
	/// localReferencePoint is in root's space; the distances are scaled by
	/// root's largest absolute scale.
	/// </summary>
	public static Vector4 Pack(Transform root, Vector3 localReferencePoint, float startDistance, float endDistance)
	{
		var scale = MaxAbs(root.lossyScale);
		var referencePoint = root.TransformPoint(localReferencePoint);
		return new Vector4(
			referencePoint.x,
			referencePoint.y,
			referencePoint.z,
			PackDistances(startDistance * scale, endDistance * scale));
	}

	public static float PackDistances(float startDistance, float endDistance)
	{
		return BitConverter.Int32BitsToSingle(
			Mathf.FloatToHalf(Mathf.Min(startDistance, MaximumHalf)) |
			Mathf.FloatToHalf(Mathf.Min(endDistance, MaximumHalf)) << 16);
	}

	public static float MaxAbs(Vector3 value)
	{
		return Mathf.Max(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
	}
}
