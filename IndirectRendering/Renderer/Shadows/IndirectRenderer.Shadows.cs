using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace IndirectRendering;

public sealed partial class IndirectRenderer
{
	private static readonly int ShadowCullingPlanesId = Shader.PropertyToID("_ShadowCullingPlanes");
	private static readonly int ShadowCullParametersId = Shader.PropertyToID("_ShadowCullParameters");
	private static readonly int ShadowRenderArgumentsId = Shader.PropertyToID("_ShadowRenderArguments");
	private static readonly int ShadowRenderVisibleOffsetsId = Shader.PropertyToID("_ShadowRenderVisibleOffsets");
	private static readonly int ShadowPrepareViewIndexId = Shader.PropertyToID("_ShadowPrepareViewIndex");
	private static readonly int ShadowPrepareFaceCountId = Shader.PropertyToID("_ShadowPrepareFaceCount");
	private static readonly int ShadowFaceCaptureId = Shader.PropertyToID("_ShadowFaceCapture");
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

	private CommandBuffer shadowResetCommandBuffer;

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
	// The cube face a point light is rendering, written by pass 1 of the
	// capture material before each face.
	private GraphicsBuffer shadowFaceCaptureBuffer;

	private int recordedShadowVersion = -1;
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

	private void RecordShadowRenderReset(CommandBuffer commandBuffer)
	{
		RecordClearInstanceCounts(commandBuffer, shadowRenderArgumentsBuffer, 0, runtimeBatches.Count);
	}

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

		// Leaves the shadow-render buffers empty for the local lights rendered after the sun.
		shadowResetCommandBuffer ??= new CommandBuffer { name = "VPO Indirect Directional Shadow Reset" };
		directionalLight.AddCommandBuffer(
			LightEvent.AfterShadowMapPass,
			shadowResetCommandBuffer,
			ShadowMapPass.Directional);
		shadowCommandBufferLight = directionalLight;
		attachedShadowCascadeCount = shadowCascadeCount;
		InvalidateShadowRecordings();
	}

	private void RecordShadowPreparationCommandBuffers()
	{
		if (recordedShadowVersion == shadowRecordingVersion) { return; }

		// The draw ranges are recorded into the command buffers, so a layout
		// change re-records them.
		var batchCount = runtimeBatches.Count;
		for (var cascadeIndex = 0; cascadeIndex < shadowCascadeCount; cascadeIndex += 1)
		{
			var commandBuffer = shadowCommandBuffers[cascadeIndex];
			commandBuffer.Clear();
			commandBuffer.BeginSample(ShadowCascadeCommandBufferNames[cascadeIndex]);

			if (cascadeIndex == 0)
			{
				// The cascades are views 1-4, right after the camera's draws.
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

			RecordShadowCascadePreparation(commandBuffer, 1 + cascadeIndex);

			commandBuffer.EndSample(ShadowCascadeCommandBufferNames[cascadeIndex]);
		}

		shadowResetCommandBuffer.Clear();
		RecordShadowRenderReset(shadowResetCommandBuffer);
		recordedShadowVersion = shadowRecordingVersion;
	#if DEBUG
		shadowPreparationRerecordCount += 1;
	#endif
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

	// Copies a cascade's or local light's view into the shadow-render buffers.
	// With six faces, viewIndex is the first and the captured face picks one.
	private void RecordShadowCascadePreparation(CommandBuffer commandBuffer, int viewIndex, int faceCount = 1)
	{
		var computeShader = shaders.FrustumCullShader;
		var kernel = shaders.PrepareShadowCascadeDrawsKernel;
		commandBuffer.BeginSample("VPO Prepare Shadow Cascade Draws");
		commandBuffer.SetComputeBufferParam(
			computeShader, kernel, IndirectArgumentsId, indirectArgumentsBuffer);
		commandBuffer.SetComputeBufferParam(computeShader, kernel, BatchDataId, batchDataBuffer);
		commandBuffer.SetComputeBufferParam(
			computeShader, kernel, ShadowRenderArgumentsId, shadowRenderArgumentsBuffer);
		commandBuffer.SetComputeBufferParam(
			computeShader, kernel, ShadowRenderVisibleOffsetsId, shadowRenderVisibleOffsetsBuffer);
		commandBuffer.SetComputeBufferParam(
			computeShader, kernel, ShadowFaceCaptureId, shadowFaceCaptureBuffer);
		// These are fixed for a recording; a batch count or visible stride
		// change re-records.
		commandBuffer.SetComputeIntParam(computeShader, ShadowPrepareViewIndexId, viewIndex);
		commandBuffer.SetComputeIntParam(computeShader, ShadowPrepareFaceCountId, faceCount);
		commandBuffer.SetComputeIntParam(computeShader, BatchCountId, runtimeBatches.Count);
		commandBuffer.SetComputeIntParam(computeShader, VisibleStrideId, visibleStride);
		commandBuffer.DispatchCompute(
			computeShader,
			kernel,
			Mathf.Max(1, ComputeDispatch.GetThreadGroupCount(
				runtimeBatches.Count, shaders.PrepareShadowCascadeDrawsGroupSize)),
			1,
			1);
		commandBuffer.EndSample("VPO Prepare Shadow Cascade Draws");
	}

	private void SubmitShadowDraws(Camera camera)
	{
		Profiler.BeginSample("ShadowDraws");
		for (var batchIndex = 0; batchIndex < runtimeBatches.Count; batchIndex += 1)
		{
			var runtimeBatch = runtimeBatches[batchIndex];
			if (!runtimeBatch.ShadowEligible || runtimeBatch.RegisteredInstanceCount == 0 ||
			    !IsShadowBatchVisible(batchIndex))
			{
				continue;
			}

			// Shadow draws read the cascade copy prepared before each cascade.
			BindInstanceBuffers(
				runtimeBatch.ShadowRenderProperties,
				shadowRenderVisibleOffsetsBuffer,
				runtimeBatch.Index,
				0);
			// The views prepared into the shadow-render buffers are culled for
			// this camera's shadows, the sun's and the local lights'.
			var renderParams = runtimeBatch.ShadowRenderParams;
			renderParams.camera = camera;
			// Unity culls each cascade and light against these, so a batch
			// only gets a draw in the shadow maps its visible instances reach.
			renderParams.worldBounds = GetShadowBatchBounds(batchIndex, runtimeBatch.WorldBounds);
			ValidateShadowSubmission(renderParams.worldBounds);
			Graphics.RenderMeshIndirect(
				renderParams,
				runtimeBatch.Mesh,
				shadowRenderArgumentsBuffer,
				1,
				runtimeBatch.Index);
		#if DEBUG
			unityShadowSubmissionCount += 1;
		#endif
		}
		Profiler.EndSample();
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
		shadowFaceCaptureBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
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

			shadowCommandBufferLight.RemoveCommandBuffer(LightEvent.AfterShadowMapPass, shadowResetCommandBuffer);
		}

		for (var index = 0; index < recordedLights.Count; index += 1)
		{
			DetachLocalShadowCommandBuffers(index);
		}

		recordedLights.Clear();

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

		shadowResetCommandBuffer?.Release();
		shadowResetCommandBuffer = null;
		foreach (var commandBuffer in localShadowCommandBuffers)
		{
			commandBuffer.Release();
		}

		localShadowCommandBuffers.Clear();
		replacementCommandBuffer?.Release();
		replacementCommandBuffer = null;
	}
}
