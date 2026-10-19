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

	public static IndirectView InSphere(Vector3 position, float radius, float lodScale)
	{
		return new IndirectView
		{
			LodOrigin = position, LodScale = lodScale, Sphere = new Vector4(position.x, position.y, position.z, radius),
		};
	}

	public static float GetLodScale(float lodBias, float fieldOfView)
	{
		return lodBias / (2f * Mathf.Tan(fieldOfView * 0.5f * Mathf.Deg2Rad));
	}
}
