using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using IndirectRendering.Burst;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace IndirectRendering;

public sealed partial class IndirectRenderer
{
	private static readonly int ShadowCullingPlanesId = Shader.PropertyToID("_ShadowCullingPlanes");
	private static readonly int ShadowCullParametersId = Shader.PropertyToID("_ShadowCullParameters");
	private static readonly int ShadowCascadeCountId = Shader.PropertyToID("_ShadowCascadeCount");

	[StructLayout(LayoutKind.Sequential)]
	private struct ShadowCullParameters
	{
		internal uint InstanceCount;
		internal uint BatchCount;
		internal uint VisibleStride;
		internal uint PlaneCount;
		// See cameraLodParameters.
		internal Vector4 LodParameters;
	}

	private const int ShadowCascadeSphereCaptureCount = MaxDirectionalCascades + 1;
	private const int ShadowCascadeCaptureWriteTarget = 1;

	private int shadowCascadeCount;

	private readonly ShadowCullParameters[] shadowCullParameters = new ShadowCullParameters[1];
	private readonly uint[] shadowDispatchArguments = [0, 1, 1];
	private GraphicsBuffer shadowCullParametersBuffer;
	private GraphicsBuffer shadowCullingPlanesBuffer;
	private GraphicsBuffer shadowDispatchArgumentsBuffer;

	// Four split spheres plus a validity marker, written on the GPU by the
	// capture draw and reset from the CPU every frame.
	private GraphicsBuffer shadowCascadeSpheresBuffer;
	private readonly Vector4[] shadowCascadeSpheresReset = new Vector4[ShadowCascadeSphereCaptureCount];
	private Material shadowCascadeCaptureMaterial;

	private int recordedShadowVersion = -1;
	private int recordedShadowCullingMask;
	// The batches the cascades' command buffers draw one by one.
	private List<int> recordedShadowBatches = new();
	private List<int> shadowBatchesScratch = new();
	private readonly CommandBuffer[] shadowCommandBuffers = new CommandBuffer[MaxDirectionalCascades];
	private Light shadowCommandBufferLight;

	private int attachedShadowCascadeCount;

	private static readonly string[] ShadowCascadeCommandBufferNames =
	{
		"VPO Indirect Directional Shadow Cascade 0",
		"VPO Indirect Directional Shadow Cascade 1",
		"VPO Indirect Directional Shadow Cascade 2",
		"VPO Indirect Directional Shadow Cascade 3",
	};

	private void UpdateShadowCommandBufferAttachments(Light directionalLight, bool hasShadowWork)
	{
		if (!hasShadowWork)
		{
			DetachShadowCommandBuffers();
			return;
		}

		if (shadowCommandBufferLight == directionalLight &&
		    attachedShadowCascadeCount == shadowCascadeCount)
		{
			return;
		}

		DetachShadowCommandBuffers();
		for (var index = 0; index < shadowCascadeCount; index += 1)
		{
			shadowCommandBuffers[index] ??= new CommandBuffer { name = ShadowCascadeCommandBufferNames[index] };
			directionalLight.AddCommandBuffer(
				LightEvent.BeforeShadowMapPass,
				shadowCommandBuffers[index],
				GetShadowMapPass(index));
		}

		shadowCommandBufferLight = directionalLight;
		attachedShadowCascadeCount = shadowCascadeCount;
		InvalidateShadowRecordings();
	}

	/// <param name="cullingMask">The directional light's.</param>
	private void RecordShadowPreparationCommandBuffers(int cullingMask)
	{
		// The batch cull left only shadow casters in the directional row, and with
		// multiDrawActive, only the ones that do not multi-draw.
		var batches = shadowBatchesScratch;
		batches.Clear();
		var drawCount = CopyViewDraws(DirectionalShadowCullRow, ref viewDrawsScratch);
		var draws = viewDrawsScratch;
		for (var index = 0; index < drawCount; index += 1)
		{
			var batchIndex = draws[index].BatchIndex;
			if ((cullingMask & (1 << runtimeBatches[batchIndex].ShadowRenderParams.layer)) != 0)
			{
				batches.Add(batchIndex);
			}
		}

		if (recordedShadowVersion == shadowRecordingVersion &&
		    recordedShadowCullingMask == cullingMask && SameBatches(recordedShadowBatches, batches))
		{
			return;
		}

		shadowBatchesScratch = recordedShadowBatches;
		recordedShadowBatches = batches;
		// The draw ranges are recorded into the command buffers, so a layout
		// change re-records them.
		for (var cascadeIndex = 0; cascadeIndex < shadowCascadeCount; cascadeIndex += 1)
		{
			var commandBuffer = shadowCommandBuffers[cascadeIndex];
			commandBuffer.Clear();
			commandBuffer.BeginSample(ShadowCascadeCommandBufferNames[cascadeIndex]);

			if (cascadeIndex == 0)
			{
				RecordShadowCull(commandBuffer);
			}

			if (multiDrawActive)
			{
				RecordMultiDrawPass(commandBuffer, cascadeMultiDrawPasses[cascadeIndex], 1 + cascadeIndex, cullingMask);
			}

			RecordCascadeDraws(commandBuffer, 1 + cascadeIndex, batches);
			commandBuffer.EndSample(ShadowCascadeCommandBufferNames[cascadeIndex]);
		}

		recordedShadowVersion = shadowRecordingVersion;
		recordedShadowCullingMask = cullingMask;
	#if DEBUG
		shadowPreparationRerecordCount += 1;
	#endif
	}

	// Culls every cascade's view, which are rows 1-4, right after the camera's.
	private void RecordShadowCull(CommandBuffer commandBuffer)
	{
		var batchCount = runtimeBatches.Count;
		RecordClearInstanceCounts(
			commandBuffer, indirectArgumentsBuffer, batchCount, shadowCascadeCount * batchCount);
		commandBuffer.BeginSample("VPO Shadow Cull All");
		var computeShader = shaders.FrustumCullShader;
		commandBuffer.SetComputeIntParam(computeShader, ShadowCascadeCountId, shadowCascadeCount);
		RecordShadowCascadeCapture(commandBuffer);
		RecordShadowReceiverMap(commandBuffer, computeShader);
		var kernel = shaders.ShadowFrustumCullKernel;
		commandBuffer.SetComputeBufferParam(
			computeShader, kernel, ShadowInputInstancesId, instanceBuffer);
		commandBuffer.SetComputeBufferParam(
			computeShader, kernel, ShadowCullingPlanesId, shadowCullingPlanesBuffer);
		commandBuffer.SetComputeBufferParam(
			computeShader, kernel, BatchDataId, batchDataBuffer);
		commandBuffer.SetComputeBufferParam(
			computeShader, kernel, ShadowCullParametersId, shadowCullParametersBuffer);
		commandBuffer.SetComputeBufferParam(
			computeShader, kernel, CullVisibleInstanceIndicesId, visibleInstanceIndicesBuffer);
		commandBuffer.SetComputeBufferParam(
			computeShader, kernel, IndirectArgumentsId, indirectArgumentsBuffer);
		commandBuffer.SetComputeBufferParam(
			computeShader, kernel, ShadowReceiverParametersId, shadowReceiverParametersBuffer);
		commandBuffer.SetComputeBufferParam(
			computeShader, kernel, ShadowReceiverMapId, shadowReceiverMapBuffer);
		commandBuffer.SetComputeBufferParam(
			computeShader, kernel, ShadowCascadeSpheresId, shadowCascadeSpheresBuffer);
		commandBuffer.SetComputeBufferParam(
			computeShader, kernel, CullDiagnosticsId, cullDiagnosticsBuffer);
		commandBuffer.SetComputeIntParam(computeShader, CullDiagnosticsEnabledId, CullDiagnosticsEnabled);
		commandBuffer.DispatchCompute(computeShader, kernel, shadowDispatchArgumentsBuffer, 0);
		commandBuffer.EndSample("VPO Shadow Cull All");
	}

	private void RecordShadowCascadeCapture(CommandBuffer commandBuffer)
	{
		// UnityShadows constants are bound for draws inside the shadow pass but
		// not for compute dispatches, so a one-point draw copies them out. It
		// writes no color or depth and leaves the shadow map untouched.
		commandBuffer.BeginSample("VPO Shadow Cascade Capture");
		commandBuffer.SetRandomWriteTarget(ShadowCascadeCaptureWriteTarget, shadowCascadeSpheresBuffer);
		commandBuffer.DrawProcedural(
			Matrix4x4.identity,
			shadowCascadeCaptureMaterial,
			0,
			MeshTopology.Points,
			1);
		commandBuffer.ClearRandomWriteTargets();
		commandBuffer.EndSample("VPO Shadow Cascade Capture");
	}

	// The directional row covers the casters of every cascade, so a batch
	// outside this cascade has no instances in its draw and draws nothing.
	private void RecordCascadeDraws(CommandBuffer commandBuffer, int viewIndex, List<int> batches)
	{
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
	}

	// Unity renders a cascade, and runs its command buffer, only when it finds
	// casters in it, and fits the shadow depth range to the casters' bounds. An
	// empty draw over every visible caster's bounds keeps both as their own draws
	// would.
	private void SubmitDirectionalShadowPlaceholder(Camera camera)
	{
		Profiler.BeginSample("DirectionalShadowPlaceholder");
		var rowBounds = GetRowBounds(DirectionalShadowCullRow);
		if (rowBounds.BatchIndex >= 0)
		{
			SubmitEmptyShadowDraw(camera, runtimeBatches[rowBounds.BatchIndex], rowBounds.Bounds);
		}

		Profiler.EndSample();
	}

	private void SubmitEmptyShadowDraw(Camera camera, RuntimeBatch runtimeBatch, Bounds bounds)
	{
		var renderParams = runtimeBatch.ShadowRenderParams;
		renderParams.camera = camera;
		renderParams.matProps = GetViewMaterialProperties(runtimeBatch, 0);
		renderParams.worldBounds = bounds;
		ValidateShadowSubmission(bounds);
		Graphics.RenderMeshIndirect(renderParams, runtimeBatch.Mesh, GetEmptyShadowArgumentsBuffer());
	#if DEBUG
		unityShadowSubmissionCount += 1;
	#endif
	}

	private void EnsureShadowFixedBuffers()
	{
		if (shadowCullParametersBuffer != null) { return; }

		shadowCullParametersBuffer = new GraphicsBuffer(
			GraphicsBuffer.Target.Structured,
			1,
			Marshal.SizeOf<ShadowCullParameters>());
		shadowCullingPlanesBuffer = new GraphicsBuffer(
			GraphicsBuffer.Target.Structured,
			MaxShadowCullingPlanes,
			sizeof(float) * 4);
		shadowDispatchArgumentsBuffer = new GraphicsBuffer(
			GraphicsBuffer.Target.IndirectArguments,
			3,
			sizeof(uint));
		// No group count is 0, so the next UploadShadowBuffers writes the new buffer.
		shadowDispatchArguments[0] = 0;
		shadowReceiverParametersBuffer = new GraphicsBuffer(
			GraphicsBuffer.Target.Structured,
			1,
			Marshal.SizeOf<ShadowReceiverParameters>());
		shadowReceiverMapBuffer = new GraphicsBuffer(
			GraphicsBuffer.Target.Structured,
			ShadowReceiverMapElementCount,
			sizeof(uint));
		shadowCascadeSpheresBuffer = new GraphicsBuffer(
			GraphicsBuffer.Target.Structured,
			ShadowCascadeSphereCaptureCount,
			sizeof(float) * 4);
		shadowCascadeCaptureMaterial = new Material(shaders.ShadowCascadeCaptureShader)
		{
			hideFlags = HideFlags.HideAndDontSave,
		};
		InvalidateShadowRecordings();
	}

	private void UploadShadowBuffers(int instanceCount)
	{
		shadowCullParameters[0] = new ShadowCullParameters
		{
			InstanceCount = (uint)instanceCount, BatchCount = (uint)runtimeBatches.Count,
			VisibleStride = (uint)visibleStride, PlaneCount = (uint)shadowPlaneCount,
			LodParameters = cameraLodParameters,
		};
		ComputeDispatch.GetDimensions(
			ComputeDispatch.GetThreadGroupCount(instanceCount, shaders.ShadowFrustumCullGroupSize),
			out var dispatchX, out var dispatchY);
		if (shadowDispatchArguments[0] != dispatchX || shadowDispatchArguments[1] != dispatchY)
		{
			shadowDispatchArguments[0] = (uint)dispatchX;
			shadowDispatchArguments[1] = (uint)dispatchY;
			shadowDispatchArgumentsBuffer.SetData(shadowDispatchArguments);
		}

		shadowCullParametersBuffer.SetData(shadowCullParameters);
		shadowCullingPlanesBuffer.SetData(encodedShadowPlanes, 0, 0, shadowPlaneCount);
	}

	private static ShadowMapPass GetShadowMapPass(int cascadeIndex)
	{
		return cascadeIndex switch
		{
			0 => ShadowMapPass.DirectionalCascade0,
			1 => ShadowMapPass.DirectionalCascade1,
			2 => ShadowMapPass.DirectionalCascade2,
			3 => ShadowMapPass.DirectionalCascade3,
			_ => throw new ArgumentOutOfRangeException(nameof(cascadeIndex)),
		};
	}

	private void DetachShadowCommandBuffers()
	{
		if (shadowCommandBufferLight)
		{
			for (var index = 0; index < attachedShadowCascadeCount; index += 1)
			{
				shadowCommandBufferLight.RemoveCommandBuffer(
					LightEvent.BeforeShadowMapPass,
					shadowCommandBuffers[index]);
			}

		}

		foreach (var buffer in localShadowBuffers)
		{
			DetachLocalShadowBuffer(buffer);
		}

		shadowCommandBufferLight = null;
		attachedShadowCascadeCount = 0;
		InvalidateShadowRecordings();
	}

	private void DisposeShadowCommandBuffers()
	{
		DetachShadowCommandBuffers();
		for (var index = 0; index < MaxDirectionalCascades; index += 1)
		{
			shadowCommandBuffers[index]?.Release();
			shadowCommandBuffers[index] = null;
		}

		DisposeLocalShadowBuffers();
		replacementCommandBuffer?.Release();
		replacementCommandBuffer = null;
	}
}
