using IndirectRendering.Extensions;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace IndirectRendering;

public sealed partial class IndirectRenderer
{
	private static readonly int MultiDrawBatchesId = Shader.PropertyToID("_MultiDrawBatches");
	private static readonly int MultiDrawArgumentsId = Shader.PropertyToID("_MultiDrawArguments");
	private static readonly int MultiDrawBatchCountId = Shader.PropertyToID("_MultiDrawBatchCount");
	private static readonly int MultiDrawViewIndexId = Shader.PropertyToID("_MultiDrawViewIndex");
	private static readonly int MultiDrawCullingMaskId = Shader.PropertyToID("_MultiDrawCullingMask");
	private static readonly int CasterVerticesId = Shader.PropertyToID("_VPOCasterVertices");
	private static readonly int CasterBatchParametersId = Shader.PropertyToID("_VPOCasterBatchParameters");

	// What one shadow pass's multi-draws need: its own prepared arguments and
	// the plugin parameters and material properties of its draws.
	private sealed class MultiDrawPass
	{
		internal readonly MultiDraw.ParamsBlock Params = new();
		internal readonly MaterialPropertyBlock Properties = new();
		// One draw per multi-draw batch, prepared by the pass's own dispatch.
		internal GraphicsBuffer Arguments;
	}

	private readonly MultiDrawCasterSet multiDrawCasters;
	private bool multiDrawOverflowLogged;
	private readonly MultiDrawPass[] cascadeMultiDrawPasses = CreateMultiDrawPasses(MaxDirectionalCascades);

	private static MultiDrawPass[] CreateMultiDrawPasses(int count)
	{
		var passes = new MultiDrawPass[count];
		for (var index = 0; index < count; index += 1)
		{
			passes[index] = new MultiDrawPass();
		}

		return passes;
	}

	// Whether this frame's shadows draw their multi-draw batches through native
	// multi-draw; the batch cull and the shadow recordings follow it.
	private bool multiDrawActive;

	private void SetMultiDrawActive(bool active)
	{
		if (multiDrawActive == active) { return; }

		multiDrawActive = active;
		InvalidateShadowRecordings();
	}

	/// <returns>Whether this frame's shadows draw their multi-draw batches through native multi-draw.</returns>
	private bool PrepareMultiDrawCasters()
	{
		if (!MultiDraw.Supported || !multiDrawCasters.HasResolver) { return false; }

		Profiler.BeginSample("MultiDrawCasterPreparation");
		var firstAdded = multiDrawCasters.Scan(runtimeBatches);
		if (firstAdded >= 0)
		{
			MarkBatchCullFlagsDirty(firstAdded);
		}

		// The recordings hold the index buffer and each group's draw range.
		if (multiDrawCasters.TryUpload())
		{
			InvalidateShadowRecordings();
		}

		// The plugin's identity buffer holds the instances' visible indices.
		var batchCount = multiDrawCasters.BatchCount;
		var usable = batchCount != 0 && visibleStride <= MultiDraw.MaxInstanceCount;
		if (!usable && batchCount != 0 && !multiDrawOverflowLogged)
		{
			multiDrawOverflowLogged = true;
			Debug.LogWarning(
				$"IndirectRendering: {visibleStride} visible instances per view exceed native multi-draw's " +
				$"{MultiDraw.MaxInstanceCount}; shadows draw each batch instead.");
		}

		Profiler.EndSample();
		return usable;
	}

	// Records the dispatch that prepares viewIndex's multi-draw arguments, then
	// one multi-draw per caster material over them. The arguments hold no
	// instances for batches outside the view or the layers of cullingMask.
	private void RecordMultiDrawPass(CommandBuffer commandBuffer, MultiDrawPass pass, int viewIndex, int cullingMask)
	{
		var batchCount = multiDrawCasters.BatchCount;
		var groups = multiDrawCasters.Groups;
		var geometry = multiDrawCasters.GeometryBuffer;
		// The command buffer was cleared before this, so nothing recorded still
		// reads the old arguments.
		var wordCount = batchCount * IndirectArgumentWordCount;
		if (pass.Arguments == null || pass.Arguments.count != wordCount)
		{
			GraphicsBufferX.Release(ref pass.Arguments);
			pass.Arguments = new GraphicsBuffer(
				GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.IndirectArguments,
				wordCount,
				sizeof(uint));
		}

		// One parameter slot per group, then the prepare event's.
		pass.Params.EnsureCapacity(groups.Count + 1);

		var computeShader = shaders.FrustumCullShader;
		var kernel = shaders.PrepareMultiDrawArgumentsKernel;
		commandBuffer.BeginSample("VPO Prepare Multi-Draw Arguments");
		commandBuffer.SetComputeBufferParam(computeShader, kernel, MultiDrawBatchesId, multiDrawCasters.BatchBuffer);
		commandBuffer.SetComputeBufferParam(computeShader, kernel, MultiDrawArgumentsId, pass.Arguments);
		commandBuffer.SetComputeBufferParam(computeShader, kernel, IndirectArgumentsId, indirectArgumentsBuffer);
		commandBuffer.SetComputeBufferParam(computeShader, kernel, BatchDataId, batchDataBuffer);
		// These are fixed for a recording; a multi-draw batch or batch count
		// change re-records.
		commandBuffer.SetComputeIntParam(computeShader, MultiDrawBatchCountId, batchCount);
		commandBuffer.SetComputeIntParam(computeShader, MultiDrawViewIndexId, viewIndex);
		commandBuffer.SetComputeIntParam(computeShader, MultiDrawCullingMaskId, cullingMask);
		commandBuffer.SetComputeIntParam(computeShader, BatchCountId, runtimeBatches.Count);
		ComputeDispatch.GetDimensions(
			ComputeDispatch.GetThreadGroupCount(batchCount, shaders.PrepareMultiDrawArgumentsGroupSize),
			out var dispatchX, out var dispatchY);
		commandBuffer.DispatchCompute(computeShader, kernel, dispatchX, dispatchY, 1);
		MultiDraw.RecordPrepare(commandBuffer, pass.Params.WritePrepare(groups.Count, pass.Arguments));
		commandBuffer.EndSample("VPO Prepare Multi-Draw Arguments");

		var properties = pass.Properties;
		properties.SetBuffer(CasterVerticesId, geometry.VertexBuffer);
		properties.SetBuffer(CasterBatchParametersId, multiDrawCasters.BatchParameterBuffer);
		properties.SetBuffer(InstanceDataId, gpuInstances.Buffer);
		properties.SetBuffer(VisibleInstanceIndicesId, visibleInstanceIndicesBuffer);
		// SetInt stores a float, which loses view bases past 2^24.
		properties.SetInteger(VisibleViewBaseId, viewIndex * visibleStride);
		for (var groupIndex = 0; groupIndex < groups.Count; groupIndex += 1)
		{
			var group = groups[groupIndex];
			var parameters = pass.Params.Write(
				groupIndex,
				pass.Arguments,
				group.FirstBatch,
				group.Batches.Count,
				geometry.IndexBuffer);
			MultiDraw.Record(commandBuffer, parameters, geometry.IndexBuffer, group.Material, 0, properties);
		}
	}

	private static void ReleaseMultiDrawPass(MultiDrawPass pass)
	{
		GraphicsBufferX.Release(ref pass.Arguments);
		pass.Params.Dispose();
	}

	private void ReleaseMultiDrawShadows()
	{
		multiDrawCasters.Release();
		foreach (var pass in cascadeMultiDrawPasses)
		{
			ReleaseMultiDrawPass(pass);
		}
	}
}
