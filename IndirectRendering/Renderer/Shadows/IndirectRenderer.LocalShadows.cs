using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace IndirectRendering;

public sealed partial class IndirectRenderer
{
	// What Unity's spot and point caster jobs draw with (D3D11 DepthBias -1,
	// SlopeScaledDepthBias -1 under reversed Z).
	private const float LocalShadowDepthBias = 1f;
	private const float LocalShadowSlopeDepthBias = 1f;

	private const float PointShadowFaceFieldOfView = 91f;

	private const float PointShadowCullPadding = 0.4f;

	private static readonly ShadowMapPass[] PointShadowFacePasses =
	{
		ShadowMapPass.PointlightPositiveX, ShadowMapPass.PointlightNegativeX,
		ShadowMapPass.PointlightPositiveY, ShadowMapPass.PointlightNegativeY,
		ShadowMapPass.PointlightPositiveZ, ShadowMapPass.PointlightNegativeZ,
	};

	private static readonly Quaternion[] PointShadowFaceRotations =
	{
		Quaternion.LookRotation(Vector3.right, Vector3.up),
		Quaternion.LookRotation(Vector3.left, Vector3.up),
		Quaternion.LookRotation(Vector3.up, Vector3.forward),
		Quaternion.LookRotation(Vector3.down, Vector3.forward),
		Quaternion.LookRotation(Vector3.forward, Vector3.up),
		Quaternion.LookRotation(Vector3.back, Vector3.up),
	};

	private readonly List<Light> shadowedLights = new();
	private readonly List<ShadowedLightView> shadowedLightViews = new();
	private int firstShadowedLightView;
	private readonly List<LocalShadowBuffer> localShadowBuffers = new();
	private List<int> localShadowBatchesScratch = new();

	private GraphicsBuffer emptyShadowArgumentsBuffer;

	private struct ShadowedLightView
	{
		internal Light Light;
		internal ShadowMapPass Pass;
	}

	private sealed class LocalShadowBuffer
	{
		internal readonly CommandBuffer Buffer = new() { name = "VPO Indirect Local Shadow Draws" };
		internal Light Light;
		internal ShadowMapPass Pass;
		internal int RecordedVersion = -1;
		internal int RecordedViewIndex;
		internal int RecordedCullingMask;
		internal List<int> RecordedBatches = new();
		internal readonly MultiDrawPass MultiDraw = new();
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
		firstShadowedLightView = FixedViewCount + views.Count;
		foreach (var light in shadowedLights)
		{
			var lightTransform = light.transform;
			var position = lightTransform.position;
			var near = Mathf.Max(0.01f, light.shadowNearPlane);
			if (light.type == LightType.Spot)
			{
				AddShadowedLightView(
					light,
					ShadowMapPass.Spotlight,
					IndirectView.Perspective(
						position,
						lightTransform.rotation,
						Mathf.Min(light.spotAngle + 2f, 179f),
						near,
						light.range,
						0f));
				continue;
			}

			for (var face = 0; face < PointShadowFacePasses.Length; face += 1)
			{
				var view = IndirectView.Perspective(
					position,
					PointShadowFaceRotations[face],
					PointShadowFaceFieldOfView,
					near,
					light.range,
					0f);
				view.CullPadding = PointShadowCullPadding;
				AddShadowedLightView(light, PointShadowFacePasses[face], view);
			}
		}
	}

	private void AddShadowedLightView(Light light, ShadowMapPass pass, IndirectView view)
	{
		view.LodOrigin = cameraLodParameters;
		view.LodScale = cameraLodParameters.w;
		view.ShadowCastersOnly = true;
		views.Add(view);
		shadowedLightViews.Add(new ShadowedLightView { Light = light, Pass = pass });
	}

	private void RecordLocalShadowCommandBuffers(bool hasShadowWork)
	{
		var viewCount = hasShadowWork ? shadowedLightViews.Count : 0;
		for (var index = 0; index < localShadowBuffers.Count; index += 1)
		{
			var buffer = localShadowBuffers[index];
			if (index >= viewCount || buffer.Light != shadowedLightViews[index].Light ||
			    buffer.Pass != shadowedLightViews[index].Pass)
			{
				DetachLocalShadowBuffer(buffer);
			}
		}

		if (viewCount == 0) { return; }

		for (var index = 0; index < viewCount; index += 1)
		{
			if (localShadowBuffers.Count == index)
			{
				localShadowBuffers.Add(new LocalShadowBuffer());
			}

			var buffer = localShadowBuffers[index];
			var lightView = shadowedLightViews[index];
			if (buffer.Light != lightView.Light)
			{
				lightView.Light.AddCommandBuffer(LightEvent.BeforeShadowMapPass, buffer.Buffer, lightView.Pass);
				buffer.Light = lightView.Light;
				buffer.Pass = lightView.Pass;
			}

			RecordLocalShadowDraws(buffer, lightView.Light.cullingMask, index);
		}
	}

	// Re-records only when the view gained a batch, or its index or the
	// recorded buffers changed; the draws' instance counts are read on the GPU,
	// so batches that left the view draw nothing until then. With
	// multiDrawActive, the multi-draw batches go in the light's native multi-draws
	// and the rest draw one by one.
	private void RecordLocalShadowDraws(LocalShadowBuffer buffer, int cullingMask, int lightViewIndex)
	{
		var batches = localShadowBatchesScratch;
		batches.Clear();
		var viewIndex = firstShadowedLightView + lightViewIndex;
		// The batch cull left only shadow casters in the view, and with
		// multiDrawActive, only the ones that do not multi-draw.
		var drawCount = CopyViewDraws(viewIndex, ref viewDrawsScratch);
		var draws = viewDrawsScratch;
		for (var index = 0; index < drawCount; index += 1)
		{
			var batchIndex = draws[index].BatchIndex;
			if ((cullingMask & (1 << runtimeBatches[batchIndex].ShadowRenderParams.layer)) != 0)
			{
				batches.Add(batchIndex);
			}
		}

		// copetimizations
		if (buffer.RecordedVersion == shadowRecordingVersion && buffer.RecordedViewIndex == viewIndex &&
		    buffer.RecordedCullingMask == cullingMask &&
		    ContainsBatches(buffer.RecordedBatches, batches))
		{
			return;
		}

		Profiler.BeginSample("LocalShadowRerecord");
		localShadowBatchesScratch = buffer.RecordedBatches;
		buffer.RecordedBatches = batches;
		buffer.RecordedVersion = shadowRecordingVersion;
		buffer.RecordedViewIndex = viewIndex;
		buffer.RecordedCullingMask = cullingMask;
		var commandBuffer = buffer.Buffer;
		commandBuffer.Clear();
		// Unity sets its caster depth bias only inside the shadow job, after this
		// buffer runs; without it the indirect casters shadow themselves.
		commandBuffer.SetGlobalDepthBias(LocalShadowDepthBias, LocalShadowSlopeDepthBias);
		if (multiDrawActive)
		{
			RecordMultiDrawPass(commandBuffer, buffer.MultiDraw, viewIndex, cullingMask);
		}

		foreach (var batchIndex in batches)
		{
			var runtimeBatch = runtimeBatches[batchIndex];
			commandBuffer.DrawMeshInstancedIndirect(
				runtimeBatch.Mesh,
				runtimeBatch.SubMeshIndex,
				runtimeBatch.ShadowRenderParams.material,
				runtimeBatch.ShadowCasterPass,
				indirectArgumentsBuffer,
				(viewIndex * runtimeBatches.Count + batchIndex) * GraphicsBuffer.IndirectDrawIndexedArgs.size,
				GetViewMaterialProperties(runtimeBatch, viewIndex));
		}

		commandBuffer.SetGlobalDepthBias(0f, 0f);
		Profiler.EndSample();
	}

	private static bool SameBatches(List<int> recorded, List<int> current)
	{
		if (recorded.Count != current.Count) { return false; }

		for (var index = 0; index < current.Count; index += 1)
		{
			if (recorded[index] != current[index]) { return false; }
		}

		return true;
	}

	private static bool ContainsBatches(List<int> recorded, List<int> current)
	{
		var recordedIndex = 0;
		for (var index = 0; index < current.Count; index += 1)
		{
			var batchIndex = current[index];
			while (recordedIndex < recorded.Count && recorded[recordedIndex] < batchIndex)
			{
				recordedIndex += 1;
			}

			if (recordedIndex == recorded.Count || recorded[recordedIndex] != batchIndex) { return false; }
		}

		return true;
	}

	// Unity skips the shadow map of a light, or a cube face, it finds no
	// casters for, and with them the command buffers that draw the indirect
	// casters. One empty draw over each light's range keeps them all.
	private void SubmitLocalShadowPlaceholders(Camera camera)
	{
		var index = 0;
		while (index < shadowedLightViews.Count)
		{
			var light = shadowedLightViews[index].Light;
			// A light none of whose views sees a caster has no shadow map to keep.
			var batchIndex = -1;
			while (index < shadowedLightViews.Count && shadowedLightViews[index].Light == light)
			{
				if (batchIndex < 0)
				{
					batchIndex = GetRowBounds(firstShadowedLightView + index).BatchIndex;
				}

				index += 1;
			}

			if (batchIndex < 0) { continue; }

			// Past the near plane on every side, but small enough to stay out of
			// most other lights' shadow maps.
			SubmitEmptyShadowDraw(
				camera,
				runtimeBatches[batchIndex],
				new Bounds(
					light.transform.position,
					Vector3.one * (Mathf.Max(0.01f, light.shadowNearPlane) * 2f + 0.5f)));
		}
	}

	private GraphicsBuffer GetEmptyShadowArgumentsBuffer()
	{
		if (emptyShadowArgumentsBuffer == null)
		{
			emptyShadowArgumentsBuffer = new GraphicsBuffer(
				GraphicsBuffer.Target.IndirectArguments,
				1,
				GraphicsBuffer.IndirectDrawIndexedArgs.size);
			emptyShadowArgumentsBuffer.SetData(new GraphicsBuffer.IndirectDrawIndexedArgs[1]);
		}

		return emptyShadowArgumentsBuffer;
	}

	private static void DetachLocalShadowBuffer(LocalShadowBuffer buffer)
	{
		if (buffer.Light)
		{
			buffer.Light.RemoveCommandBuffer(LightEvent.BeforeShadowMapPass, buffer.Buffer);
		}

		buffer.Light = null;
		buffer.Buffer.Clear();
		buffer.RecordedVersion = -1;
	}

	private void DisposeLocalShadowBuffers()
	{
		foreach (var buffer in localShadowBuffers)
		{
			DetachLocalShadowBuffer(buffer);
			buffer.Buffer.Release();
			ReleaseMultiDrawPass(buffer.MultiDraw);
		}

		localShadowBuffers.Clear();
	}
}
