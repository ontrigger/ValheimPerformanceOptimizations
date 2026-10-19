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

	// Shadowed light i has view firstShadowedLightView + i: a frustum for a
	// spot light, everything in range for a point light.
	private readonly List<Light> shadowedLights = new();
	private int firstShadowedLightView;
	// Shadowed light i draws through localShadowBuffers[i].
	private readonly List<LocalShadowBuffer> localShadowBuffers = new();
	private List<int> localShadowBatchesScratch = new();

	private GraphicsBuffer emptyShadowArgumentsBuffer;

	// Draws a light's view before each of its shadow passes: once for a spot
	// light, before every cube face for a point light.
	private sealed class LocalShadowBuffer
	{
		internal readonly CommandBuffer Buffer = new() { name = "VPO Indirect Local Shadow Draws" };
		internal Light Light;
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

		firstShadowedLightView = FixedViewCount + views.Count;
		foreach (var light in shadowedLights)
		{
			var lightTransform = light.transform;
			var view = light.type == LightType.Spot
				? IndirectView.Perspective(
					lightTransform.position,
					lightTransform.rotation,
					Mathf.Min(light.spotAngle + 2f, 179f),
					Mathf.Max(0.01f, light.shadowNearPlane),
					light.range,
					0f)
				: IndirectView.InSphere(lightTransform.position, light.range, 0f);
			view.LodOrigin = cameraLodParameters;
			view.LodScale = cameraLodParameters.w;
			view.ShadowCastersOnly = true;
			views.Add(view);
		}
	}

	private void RecordLocalShadowCommandBuffers(bool hasShadowWork)
	{
		var lightCount = hasShadowWork ? shadowedLights.Count : 0;
		for (var index = 0; index < localShadowBuffers.Count; index += 1)
		{
			var buffer = localShadowBuffers[index];
			if (index >= lightCount || buffer.Light != shadowedLights[index])
			{
				DetachLocalShadowBuffer(buffer);
			}
		}

		if (lightCount == 0) { return; }

		for (var index = 0; index < lightCount; index += 1)
		{
			if (localShadowBuffers.Count == index)
			{
				localShadowBuffers.Add(new LocalShadowBuffer());
			}

			var buffer = localShadowBuffers[index];
			var light = shadowedLights[index];
			if (buffer.Light != light)
			{
				var pass = light.type == LightType.Spot ? ShadowMapPass.Spotlight : ShadowMapPass.Pointlight;
				light.AddCommandBuffer(LightEvent.BeforeShadowMapPass, buffer.Buffer, pass);
				buffer.Light = light;
			}

			RecordLocalShadowDraws(buffer, light.cullingMask, index);
		}
	}

	// Re-records only when the view's batches, its index or the recorded
	// buffers changed; the draws' instance counts are read on the GPU. With
	// multiDrawActive, the multi-draw batches go in the light's native multi-draws
	// and the rest draw one by one.
	private void RecordLocalShadowDraws(LocalShadowBuffer buffer, int cullingMask, int lightIndex)
	{
		var batches = localShadowBatchesScratch;
		batches.Clear();
		var viewIndex = firstShadowedLightView + lightIndex;
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
		    SameBatches(buffer.RecordedBatches, batches))
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

	// Unity skips the shadow map of a light, or a cube face, it finds no
	// casters for, and with them the command buffers that draw the indirect
	// casters. One empty draw over each light's range keeps them all.
	private void SubmitLocalShadowPlaceholders(Camera camera)
	{
		for (var index = 0; index < shadowedLights.Count; index += 1)
		{
			// A light that sees no caster has no shadow map to keep.
			var batchIndex = GetRowBounds(firstShadowedLightView + index).BatchIndex;
			if (batchIndex < 0) { continue; }

			// Past the near plane on every side, but small enough to stay out of
			// most other lights' shadow maps.
			var light = shadowedLights[index];
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
