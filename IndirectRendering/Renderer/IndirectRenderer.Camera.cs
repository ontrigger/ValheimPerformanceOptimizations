using UnityEngine;
using UnityEngine.Profiling;

namespace IndirectRendering;

public sealed partial class IndirectRenderer
{
	private static readonly int ViewProjectionMatrixId = Shader.PropertyToID("_ViewProjectionMatrix");
	private static readonly int MinimumScreenPercentageId = Shader.PropertyToID("_MinimumScreenPercentage");

	private readonly Plane[] cameraFrustumPlanes = new Plane[6];

	// Main camera position and lodBias / (2 tan(fov / 2)), read by the camera
	// view, the shadow views and the shadow cull; see IsInLodRange in
	// PieceFrustumCull.compute.
	private Vector4 cameraLodParameters;

	private void UpdateCameraLodParameters(Camera camera)
	{
		var lodScale = IndirectView.GetLodScale(QualitySettings.lodBias, camera.fieldOfView);
		var position = camera.transform.position;
		cameraLodParameters = new Vector4(position.x, position.y, position.z, lodScale);
	}

	private void SubmitCameraDraws(Camera camera)
	{
		Profiler.BeginSample("CameraDraws");
		var submissionCount = SubmitViewDraws(0, camera);
	#if DEBUG
		cameraSubmissionCount = submissionCount;
	#endif
		Profiler.EndSample();
	}
}
