using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace IndirectRendering;

public sealed partial class IndirectRenderer
{
	private const int CubeFaceCount = 6;

	// Shadowed light i takes command buffers 2i (before each of its shadow
	// passes) and 2i + 1 (after), and its views start at shadowedLightViews[i]:
	// one for a spot light, six cube faces for a point light.
	private readonly List<Light> shadowedLights = new();
	private readonly List<int> shadowedLightViews = new();
	private readonly List<RecordedLight> recordedLights = new();
	private readonly List<CommandBuffer> localShadowCommandBuffers = new();
	private int recordedLocalShadowVersion = -1;

	// What a shadowed light's command buffers were recorded for.
	private struct RecordedLight
	{
		internal Light Light;
		internal int ViewIndex;
		internal int FaceCount;
	}

	private void CollectShadowedLights(IReadOnlyList<Light> candidates)
	{
		shadowedLights.Clear();
		for (var index = 0; index < candidates.Count; index += 1)
		{
			var light = candidates[index];
			if (light && light.isActiveAndEnabled &&
			    GeometryUtility.TestPlanesAABB(
				    cameraFrustumPlanes,
				    new Bounds(light.transform.position, Vector3.one * (light.range * 2f))))
			{
				shadowedLights.Add(light);
			}
		}

		shadowedLightViews.Clear();
		foreach (var light in shadowedLights)
		{
			shadowedLightViews.Add(FixedViewCount + views.Count);
			var lightTransform = light.transform;
			var near = Mathf.Max(0.01f, light.shadowNearPlane);
			if (light.type == LightType.Spot)
			{
				AddShadowedLightView(IndirectView.Perspective(
					lightTransform.position,
					lightTransform.rotation,
					Mathf.Min(light.spotAngle + 2f, 179f),
					near,
					light.range,
					0f));
				continue;
			}

			for (var face = 0; face < CubeFaceCount; face += 1)
			{
				AddShadowedLightView(IndirectView.CubeFace(lightTransform.position, face, near, light.range, 0f));
			}
		}
	}

	private void AddShadowedLightView(IndirectView view)
	{
		view.LodOrigin = cameraLodParameters;
		view.LodScale = cameraLodParameters.w;
		view.ShadowCastersOnly = true;
		views.Add(view);
	}

	// Each selected light copies its view into the shadow-render buffers before
	// its shadow passes and empties them after. The sun empties them after its
	// passes too, so every other light finds them empty and draws no indirect
	// casters without recording anything.
	private void RecordLocalShadowCommandBuffers(bool hasShadowWork)
	{
		var lightCount = hasShadowWork ? shadowedLights.Count : 0;
		// The recordings hold buffers and the batch count, which the version tracks.
		var versionChanged = recordedLocalShadowVersion != shadowRecordingVersion;
		recordedLocalShadowVersion = shadowRecordingVersion;
		for (var index = 0; index < recordedLights.Count; index += 1)
		{
			if (index >= lightCount || recordedLights[index].Light != shadowedLights[index])
			{
				DetachLocalShadowCommandBuffers(index);
			}
		}

		for (var index = 0; index < lightCount; index += 1)
		{
			var light = shadowedLights[index];
			var recording = new RecordedLight
			{
				Light = light,
				ViewIndex = shadowedLightViews[index],
				FaceCount = light.type == LightType.Point ? CubeFaceCount : 1,
			};
			var attached = index < recordedLights.Count && recordedLights[index].Light == light;
			if (attached && recordedLights[index].FaceCount != recording.FaceCount)
			{
				// The light changed type, so its buffers move to the other pass.
				DetachLocalShadowCommandBuffers(index);
				attached = false;
			}

			if (attached && !versionChanged && recordedLights[index].ViewIndex == recording.ViewIndex)
			{
				continue;
			}

			if (localShadowCommandBuffers.Count <= index * 2)
			{
				localShadowCommandBuffers.Add(new CommandBuffer { name = "VPO Indirect Local Shadow Prepare" });
				localShadowCommandBuffers.Add(new CommandBuffer { name = "VPO Indirect Local Shadow Reset" });
			}

			var before = localShadowCommandBuffers[index * 2];
			var after = localShadowCommandBuffers[index * 2 + 1];
			before.Clear();
			after.Clear();
			if (recording.FaceCount != 1)
			{
				RecordShadowFaceCapture(before);
			}

			RecordShadowCascadePreparation(before, recording.ViewIndex, recording.FaceCount);
			RecordShadowRenderReset(after);

			if (!attached)
			{
				var pass = light.type == LightType.Spot ? ShadowMapPass.Spotlight : ShadowMapPass.Pointlight;
				light.AddCommandBuffer(LightEvent.BeforeShadowMapPass, before, pass);
				light.AddCommandBuffer(LightEvent.AfterShadowMapPass, after, pass);
			}

			if (index < recordedLights.Count)
			{
				recordedLights[index] = recording;
			}
			else
			{
				recordedLights.Add(recording);
			}
		}

		if (recordedLights.Count > lightCount)
		{
			recordedLights.RemoveRange(lightCount, recordedLights.Count - lightCount);
		}
	}

	private void DetachLocalShadowCommandBuffers(int index)
	{
		var light = recordedLights[index].Light;
		var before = localShadowCommandBuffers[index * 2];
		var after = localShadowCommandBuffers[index * 2 + 1];
		if (light)
		{
			light.RemoveCommandBuffer(LightEvent.BeforeShadowMapPass, before);
			light.RemoveCommandBuffer(LightEvent.AfterShadowMapPass, after);
		}

		before.Clear();
		after.Clear();
	}

	// Like RecordShadowCascadeCapture, for the face matrices that are only bound
	// inside the face's pass.
	private void RecordShadowFaceCapture(CommandBuffer commandBuffer)
	{
		commandBuffer.SetRandomWriteTarget(ShadowCascadeCaptureWriteTarget, shadowFaceCaptureBuffer);
		commandBuffer.DrawProcedural(
			Matrix4x4.identity,
			shadowCascadeCaptureMaterial,
			1,
			MeshTopology.Points,
			1);
		commandBuffer.ClearRandomWriteTargets();
	}
}
