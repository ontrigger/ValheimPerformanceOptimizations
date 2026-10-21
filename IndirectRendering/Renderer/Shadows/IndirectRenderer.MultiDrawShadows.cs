using System.Collections.Generic;
using System.Runtime.InteropServices;
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

	// Must match PieceFrustumCull.compute's MultiDrawBatch.
	[StructLayout(LayoutKind.Sequential)]
	private struct MultiDrawBatch
	{
		internal uint BatchIndex;
		internal uint IndexCount;
		internal uint StartIndex;
		internal uint LayerMask;
	}

	// What one shadow pass's multi-draws need: its own prepared arguments and
	// the plugin parameters and material properties of its draws.
	private sealed class MultiDrawPass
	{
		internal readonly MultiDraw.ParamsBlock Params = new();
		internal readonly MaterialPropertyBlock Properties = new();
		// multiDrawBatchCount draws, prepared by the pass's own dispatch.
		internal GraphicsBuffer Arguments;
	}

	// The batches whose casters share a caster material.
	private sealed class MultiDrawGroup
	{
		internal Material Material;
		internal readonly List<MultiDrawBatch> Batches = new();
		// Where Batches start in multiDrawBatchBuffer.
		internal int FirstBatch;
	}

	// Casters that multiDrawCasterResolver gives a caster material, with
	// triangle meshes that have normals, draw through one native multi-draw
	// per caster material and light; see MultiDraw and CasterGeometry.
	private readonly MultiDrawCasterResolver multiDrawCasterResolver;
	private readonly CasterGeometry casterGeometry = new();
	private readonly List<MultiDrawGroup> multiDrawGroups = new();
	private readonly List<MultiDrawBatch> multiDrawBatchesScratch = new();
	// Indexed by batch: the resolver's batch parameters, read by the caster
	// shader through the batch index its instances carry.
	private readonly List<Vector4> casterBatchParameters = new();
	// Batches below this index were checked for multi-draw.
	private int multiDrawScannedBatchCount;
	private int multiDrawBatchCount;
	private bool multiDrawGeometryDirty;
	private bool multiDrawOverflowLogged;
	private GraphicsBuffer casterBatchParameterBuffer;
	private GraphicsBuffer multiDrawBatchBuffer;
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
		if (!MultiDraw.Supported || multiDrawCasterResolver == null) { return false; }

		Profiler.BeginSample("MultiDrawCasterPreparation");
		ScanMultiDrawBatches();
		if (multiDrawGeometryDirty)
		{
			UploadMultiDrawGeometry();
		}

		// The plugin's identity buffer holds the instances' visible indices.
		var usable = multiDrawBatchCount != 0 && visibleStride <= MultiDraw.MaxInstanceCount;
		if (!usable && multiDrawBatchCount != 0 && !multiDrawOverflowLogged)
		{
			multiDrawOverflowLogged = true;
			Debug.LogWarning(
				$"IndirectRendering: {visibleStride} visible instances per view exceed native multi-draw's " +
				$"{MultiDraw.MaxInstanceCount}; shadows draw each batch instead.");
		}

		Profiler.EndSample();
		return usable;
	}

	private void ScanMultiDrawBatches()
	{
		if (multiDrawScannedBatchCount == runtimeBatches.Count) { return; }

		for (; multiDrawScannedBatchCount < runtimeBatches.Count; multiDrawScannedBatchCount += 1)
		{
			var runtimeBatch = runtimeBatches[multiDrawScannedBatchCount];
			if (!runtimeBatch.ShadowEligible ||
			    !multiDrawCasterResolver(
				    runtimeBatch.ShadowRenderParams.material, out var casterMaterial, out var batchParameters) ||
			    !casterMaterial ||
			    !casterGeometry.TryGetIndexRange(runtimeBatch.Mesh, runtimeBatch.SubMeshIndex, out var indexRange))
			{
				continue;
			}

			var group = multiDrawGroups.Find(candidate => candidate.Material == casterMaterial);
			if (group == null)
			{
				group = new MultiDrawGroup { Material = casterMaterial };
				multiDrawGroups.Add(group);
			}

			while (casterBatchParameters.Count <= runtimeBatch.Index)
			{
				casterBatchParameters.Add(Vector4.zero);
			}

			casterBatchParameters[runtimeBatch.Index] = batchParameters;

			group.Batches.Add(new MultiDrawBatch
			{
				BatchIndex = (uint)runtimeBatch.Index,
				IndexCount = (uint)indexRange.IndexCount,
				StartIndex = (uint)indexRange.StartIndex,
				LayerMask = 1u << runtimeBatch.ShadowRenderParams.layer,
			});
			runtimeBatch.MultiDrawCaster = true;
			MarkBatchCullFlagsDirty(runtimeBatch.Index);
			multiDrawGeometryDirty = true;
		}
	}

	// Rebuilds every shared buffer; batches are added rarely after loading.
	private void UploadMultiDrawGeometry()
	{
		multiDrawGeometryDirty = false;
		casterGeometry.Upload();
		ReleaseBuffer(ref casterBatchParameterBuffer);
		ReleaseBuffer(ref multiDrawBatchBuffer);
		casterBatchParameterBuffer = new GraphicsBuffer(
			GraphicsBuffer.Target.Structured,
			casterBatchParameters.Count,
			sizeof(float) * 4);
		casterBatchParameterBuffer.SetData(casterBatchParameters);

		var batches = multiDrawBatchesScratch;
		batches.Clear();
		foreach (var group in multiDrawGroups)
		{
			group.FirstBatch = batches.Count;
			batches.AddRange(group.Batches);
		}

		multiDrawBatchCount = batches.Count;
		multiDrawBatchBuffer = new GraphicsBuffer(
			GraphicsBuffer.Target.Structured,
			multiDrawBatchCount,
			Marshal.SizeOf<MultiDrawBatch>());
		multiDrawBatchBuffer.SetData(batches);
		// The recordings hold the index buffer and each group's draw range.
		InvalidateShadowRecordings();
	}

	// Records the dispatch that prepares viewIndex's multi-draw arguments, then
	// one multi-draw per caster material over them. The arguments hold no
	// instances for batches outside the view or the layers of cullingMask.
	private void RecordMultiDrawPass(CommandBuffer commandBuffer, MultiDrawPass pass, int viewIndex, int cullingMask)
	{
		// The command buffer was cleared before this, so nothing recorded still
		// reads the old arguments.
		var wordCount = multiDrawBatchCount * IndirectArgumentWordCount;
		if (pass.Arguments == null || pass.Arguments.count != wordCount)
		{
			ReleaseBuffer(ref pass.Arguments);
			pass.Arguments = new GraphicsBuffer(
				GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.IndirectArguments,
				wordCount,
				sizeof(uint));
		}

		// One parameter slot per group, then the prepare event's.
		pass.Params.EnsureCapacity(multiDrawGroups.Count + 1);

		var computeShader = shaders.FrustumCullShader;
		var kernel = shaders.PrepareMultiDrawArgumentsKernel;
		commandBuffer.BeginSample("VPO Prepare Multi-Draw Arguments");
		commandBuffer.SetComputeBufferParam(computeShader, kernel, MultiDrawBatchesId, multiDrawBatchBuffer);
		commandBuffer.SetComputeBufferParam(computeShader, kernel, MultiDrawArgumentsId, pass.Arguments);
		commandBuffer.SetComputeBufferParam(computeShader, kernel, IndirectArgumentsId, indirectArgumentsBuffer);
		commandBuffer.SetComputeBufferParam(computeShader, kernel, BatchDataId, batchDataBuffer);
		// These are fixed for a recording; a multi-draw batch or batch count
		// change re-records.
		commandBuffer.SetComputeIntParam(computeShader, MultiDrawBatchCountId, multiDrawBatchCount);
		commandBuffer.SetComputeIntParam(computeShader, MultiDrawViewIndexId, viewIndex);
		commandBuffer.SetComputeIntParam(computeShader, MultiDrawCullingMaskId, cullingMask);
		commandBuffer.SetComputeIntParam(computeShader, BatchCountId, runtimeBatches.Count);
		ComputeDispatch.GetDimensions(
			ComputeDispatch.GetThreadGroupCount(multiDrawBatchCount, shaders.PrepareMultiDrawArgumentsGroupSize),
			out var dispatchX, out var dispatchY);
		commandBuffer.DispatchCompute(computeShader, kernel, dispatchX, dispatchY, 1);
		MultiDraw.RecordPrepare(commandBuffer, pass.Params.WritePrepare(multiDrawGroups.Count, pass.Arguments));
		commandBuffer.EndSample("VPO Prepare Multi-Draw Arguments");

		var properties = pass.Properties;
		properties.SetBuffer(CasterVerticesId, casterGeometry.VertexBuffer);
		properties.SetBuffer(CasterBatchParametersId, casterBatchParameterBuffer);
		properties.SetBuffer(InstanceDataId, instanceBuffer);
		properties.SetBuffer(VisibleInstanceIndicesId, visibleInstanceIndicesBuffer);
		// SetInt stores a float, which loses view bases past 2^24.
		properties.SetInteger(VisibleViewBaseId, viewIndex * visibleStride);
		for (var groupIndex = 0; groupIndex < multiDrawGroups.Count; groupIndex += 1)
		{
			var group = multiDrawGroups[groupIndex];
			var parameters = pass.Params.Write(
				groupIndex,
				pass.Arguments,
				group.FirstBatch,
				group.Batches.Count,
				casterGeometry.IndexBuffer);
			MultiDraw.Record(commandBuffer, parameters, casterGeometry.IndexBuffer, group.Material, 0, properties);
		}
	}

	private static void ReleaseMultiDrawPass(MultiDrawPass pass)
	{
		ReleaseBuffer(ref pass.Arguments);
		pass.Params.Dispose();
	}

	private void ReleaseMultiDrawShadows()
	{
		casterGeometry.Release();
		ReleaseBuffer(ref casterBatchParameterBuffer);
		ReleaseBuffer(ref multiDrawBatchBuffer);
		foreach (var pass in cascadeMultiDrawPasses)
		{
			ReleaseMultiDrawPass(pass);
		}

		multiDrawGroups.Clear();
		multiDrawScannedBatchCount = 0;
		multiDrawBatchCount = 0;
		multiDrawGeometryDirty = false;
		casterBatchParameters.Clear();
	}
}
