using UnityEngine;

namespace IndirectRendering;

/// <summary>
/// A view culled by IndirectRenderer besides the main camera and its
/// directional cascades: a reflection face, the sky depth camera or a local
/// shadow light. See IndirectRenderer.QueueView.
/// </summary>
public struct IndirectView
{
	public const float Lod0Only = float.MaxValue;

	// projection * worldToCamera
	public Matrix4x4 CullingMatrix;
	public bool HasFrustum;
	public Vector3 LodOrigin;
	public float LodScale;
	// xyz center, w radius; w <= 0 disables the test.
	public Vector4 Sphere;
	public bool ShadowCastersOnly;
	// Set on the first of six consecutive cube face views, which share its
	// sphere and LOD tests.
	public bool CubeFaces;

	public static IndirectView FromCamera(Camera camera, float lodScale)
	{
		return new IndirectView
		{
			CullingMatrix = camera.projectionMatrix * camera.worldToCameraMatrix, HasFrustum = true,
			LodOrigin = camera.transform.position, LodScale = lodScale,
		};
	}

	public static IndirectView Perspective(
		Vector3 position,
		Quaternion rotation,
		float fieldOfView,
		float near,
		float far,
		float lodScale)
	{
		// Cameras look down -z in view space.
		var worldToView = Matrix4x4.TRS(position, rotation, new Vector3(1f, 1f, -1f)).inverse;
		return new IndirectView
		{
			CullingMatrix = Matrix4x4.Perspective(fieldOfView, 1f, near, far) * worldToView, HasFrustum = true,
			LodOrigin = position, LodScale = lodScale, Sphere = new Vector4(position.x, position.y, position.z, far),
		};
	}

	// Forward and up of each cube face, in cubemap order: +X, -X, +Y, -Y, +Z, -Z.
	private static readonly Quaternion[] CubeFaceRotations =
	{
		Quaternion.LookRotation(Vector3.right, Vector3.up),
		Quaternion.LookRotation(Vector3.left, Vector3.up),
		Quaternion.LookRotation(Vector3.up, Vector3.forward),
		Quaternion.LookRotation(Vector3.down, Vector3.forward),
		Quaternion.LookRotation(Vector3.forward, Vector3.up),
		Quaternion.LookRotation(Vector3.back, Vector3.up),
	};

	public static IndirectView CubeFace(Vector3 position, int face, float near, float far, float lodScale)
	{
		// A little wider than 90 degrees for casters Unity filters across face seams.
		var view = Perspective(position, CubeFaceRotations[face], 92f, near, far, lodScale);
		view.CubeFaces = face == 0;
		return view;
	}

	public static float GetLodScale(float lodBias, float fieldOfView)
	{
		return lodBias / (2f * Mathf.Tan(fieldOfView * 0.5f * Mathf.Deg2Rad));
	}
}
