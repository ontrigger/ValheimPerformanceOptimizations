using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace IndirectRendering;

public sealed partial class IndirectRenderer
{
	private static readonly int InstanceDataId = Shader.PropertyToID("_VPOInstanceData");
	private static readonly int InstanceOverridesId = Shader.PropertyToID("_VPOInstanceOverrides");
	private static readonly int VisibleInstanceIndicesId = Shader.PropertyToID("_VPOVisibleInstanceIndices");
	private static readonly int VisibleInstanceOffsetsId = Shader.PropertyToID("_VPOVisibleInstanceOffsets");
	private static readonly int VisibleInstanceOffsetIndexId = Shader.PropertyToID("_VPOVisibleInstanceOffsetIndex");
	private static readonly int VisibleViewBaseId = Shader.PropertyToID("_VPOVisibleViewBase");

	// Set on BatchData.VisibleCapacityAndFlags for batches that cast shadows.
	private const uint ShadowCasterBatchFlag = 0x80000000u;

	[StructLayout(LayoutKind.Sequential)]
	private struct BatchData
	{
		internal Vector3 LocalBoundsCenter;
		internal uint VisibleOffset;
		internal Vector3 LocalBoundsExtents;
		internal uint VisibleCapacityAndFlags;
	}

	internal sealed class RuntimeBatch
	{
		// The batch id, and the stable index into every per-batch GPU array,
		// for the camera and the shadow cascades alike. Batches live until the
		// scene is reset.
		internal int Index;
		internal int SubMeshIndex;
		internal int RegisteredInstanceCount;
		internal uint VisibleOffset;
		internal uint VisibleCapacity;
		// Grows with registrations and resets when the batch empties; only
		// Unity's coarse cull of the whole draw reads it.
		internal float3 WorldBoundsMinimum;
		internal float3 WorldBoundsMaximum;

		internal Mesh Mesh;
		internal RenderParams CameraRenderParams;
		internal RenderParams ShadowRenderParams;
		internal Bounds MeshBounds;
		internal bool ShadowEligible;
		internal GraphicsBuffer.IndirectDrawIndexedArgs DrawArguments;
		internal readonly BoundProperties ShadowRenderProperties = new();
		// Indexed by view
		internal readonly List<BoundProperties> ViewProperties = new();

		internal Bounds WorldBounds =>
			new((WorldBoundsMinimum + WorldBoundsMaximum) * 0.5f, WorldBoundsMaximum - WorldBoundsMinimum);
	}

	// A batch's property block and the bindings version it was bound at.
	internal sealed class BoundProperties
	{
		internal readonly MaterialPropertyBlock Block = new();
		internal int Version = -1;
	}

	private const int MinimumVisibleRangeHeadroom = 16;

	// Batch layout shared by every view. Draw v * batchCount + b is batch b in
	// view v, and its visible range starts at v * visibleStride + b.VisibleOffset.
	// Everything uploaded is per batch; ClearInstanceCounts writes the draws.
	private BatchData[] batchData = [];
	private uint[] batchVisibleOffsets = [];
	private uint[] batchArguments = [];
	private GraphicsBuffer batchDataBuffer;
	private GraphicsBuffer visibleOffsetsBuffer;
	private GraphicsBuffer batchArgumentsBuffer;
	private GraphicsBuffer indirectArgumentsBuffer;
	private GraphicsBuffer visibleInstanceIndicesBuffer;

	// Unity-facing copies of one cascade's draws, refilled before each cascade.
	private GraphicsBuffer shadowRenderArgumentsBuffer;
	private GraphicsBuffer shadowRenderVisibleOffsetsBuffer;
	private int batchCapacity;
	private int visibleStride;
	private int viewCapacity = FixedViewCount;
	private int allocatedViewCapacity;
	// The batch count the current layout was built for.
	private int layoutBatchCount;
	private bool layoutDirty = true;

	private static readonly int ClearArgumentsId = Shader.PropertyToID("_ClearArguments");
	private static readonly int ClearDrawStartId = Shader.PropertyToID("_ClearDrawStart");
	private static readonly int ClearDrawCountId = Shader.PropertyToID("_ClearDrawCount");
	private static readonly int BatchArgumentsId = Shader.PropertyToID("_BatchArguments");

	// Per batch: index count, start index, base vertex and padding.
	private const int BatchArgumentWordCount = 4;

	// Resets drawCount draws in arguments from drawStart to their batch's
	// arguments with no instances, which the culls then count up again.
	private void ClearInstanceCounts(GraphicsBuffer arguments, int drawStart, int drawCount)
	{
		if (drawCount == 0) { return; }

		var computeShader = shaders.FrustumCullShader;
		var kernel = shaders.ClearInstanceCountsKernel;
		computeShader.SetBuffer(kernel, ClearArgumentsId, arguments);
		computeShader.SetBuffer(kernel, BatchArgumentsId, batchArgumentsBuffer);
		computeShader.SetInt(BatchCountId, runtimeBatches.Count);
		computeShader.SetInt(ClearDrawStartId, drawStart);
		computeShader.SetInt(ClearDrawCountId, drawCount);
		ComputeDispatch.GetDimensions(
			ComputeDispatch.GetThreadGroupCount(drawCount, shaders.ClearInstanceCountsGroupSize),
			out var dispatchX, out var dispatchY);
		computeShader.Dispatch(kernel, dispatchX, dispatchY, 1);
	}

	private void RecordClearInstanceCounts(
		CommandBuffer commandBuffer,
		GraphicsBuffer arguments,
		int drawStart,
		int drawCount)
	{
		if (drawCount == 0) { return; }

		var computeShader = shaders.FrustumCullShader;
		var kernel = shaders.ClearInstanceCountsKernel;
		commandBuffer.SetComputeBufferParam(computeShader, kernel, ClearArgumentsId, arguments);
		commandBuffer.SetComputeBufferParam(computeShader, kernel, BatchArgumentsId, batchArgumentsBuffer);
		commandBuffer.SetComputeIntParam(computeShader, BatchCountId, runtimeBatches.Count);
		commandBuffer.SetComputeIntParam(computeShader, ClearDrawStartId, drawStart);
		commandBuffer.SetComputeIntParam(computeShader, ClearDrawCountId, drawCount);
		ComputeDispatch.GetDimensions(
			ComputeDispatch.GetThreadGroupCount(drawCount, shaders.ClearInstanceCountsGroupSize),
			out var dispatchX, out var dispatchY);
		commandBuffer.DispatchCompute(computeShader, kernel, dispatchX, dispatchY, 1);
	}

	// Rebuilt when a batch is added or outgrows its visible range, or the
	// views outgrow their capacity.
	private void EnsureBatchLayout()
	{
		if (!layoutDirty) { return; }

		var batchCount = runtimeBatches.Count;
		var visibleOffset = 0;
		foreach (var runtimeBatch in runtimeBatches)
		{
			var visibleCapacity = GetVisibleRangeCapacity(runtimeBatch.RegisteredInstanceCount);
			runtimeBatch.VisibleOffset = (uint)visibleOffset;
			runtimeBatch.VisibleCapacity = (uint)visibleCapacity;
			visibleOffset = checked(visibleOffset + visibleCapacity);
		}

		var visibleStride = Mathf.NextPowerOfTwo(Mathf.Max(1, visibleOffset));
		var viewCapacityGrew = allocatedViewCapacity < viewCapacity;
		if (visibleInstanceIndicesBuffer == null || this.visibleStride < visibleStride || viewCapacityGrew)
		{
			visibleInstanceIndicesBuffer?.Release();
			this.visibleStride = Mathf.Max(this.visibleStride, visibleStride);
			visibleInstanceIndicesBuffer = new GraphicsBuffer(
				GraphicsBuffer.Target.Structured,
				checked(this.visibleStride * viewCapacity),
				sizeof(uint));
			InvalidateMaterialBindings();
		}

		var batchCapacity = Mathf.NextPowerOfTwo(Mathf.Max(1, batchCount));
		if (batchDataBuffer == null || this.batchCapacity < batchCapacity || viewCapacityGrew)
		{
			ReleaseBatchBuffers();
			this.batchCapacity = Mathf.Max(this.batchCapacity, batchCapacity);
			batchDataBuffer = new GraphicsBuffer(
				GraphicsBuffer.Target.Structured,
				this.batchCapacity,
				Marshal.SizeOf<BatchData>());
			visibleOffsetsBuffer = new GraphicsBuffer(
				GraphicsBuffer.Target.Structured,
				this.batchCapacity,
				sizeof(uint));
			batchArgumentsBuffer = new GraphicsBuffer(
				GraphicsBuffer.Target.Structured,
				this.batchCapacity,
				BatchArgumentWordCount * sizeof(uint));
			indirectArgumentsBuffer = new GraphicsBuffer(
				GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.IndirectArguments,
				viewCapacity * this.batchCapacity * IndirectArgumentWordCount,
				sizeof(uint));
			shadowRenderArgumentsBuffer = new GraphicsBuffer(
				GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.IndirectArguments,
				this.batchCapacity * IndirectArgumentWordCount,
				sizeof(uint));
			shadowRenderVisibleOffsetsBuffer = new GraphicsBuffer(
				GraphicsBuffer.Target.Structured,
				this.batchCapacity,
				sizeof(uint));
			// Zero instances until a shadow pass prepares its view.
			shadowRenderArgumentsBuffer.SetData(new uint[this.batchCapacity * IndirectArgumentWordCount]);
			InvalidateMaterialBindings();
		}

		allocatedViewCapacity = viewCapacity;
		Array.Resize(ref batchData, batchCount);
		Array.Resize(ref batchVisibleOffsets, batchCount);
		Array.Resize(ref batchArguments, batchCount * BatchArgumentWordCount);
		for (var batchIndex = 0; batchIndex < batchCount; batchIndex += 1)
		{
			var runtimeBatch = runtimeBatches[batchIndex];
			batchData[batchIndex] = new BatchData
			{
				LocalBoundsCenter = runtimeBatch.MeshBounds.center, VisibleOffset = runtimeBatch.VisibleOffset,
				LocalBoundsExtents = runtimeBatch.MeshBounds.extents, VisibleCapacityAndFlags
					= runtimeBatch.VisibleCapacity
					| (runtimeBatch.ShadowEligible ? ShadowCasterBatchFlag : 0u),
			};
			batchVisibleOffsets[batchIndex] = runtimeBatch.VisibleOffset;
			var drawArguments = runtimeBatch.DrawArguments;
			var argumentIndex = batchIndex * BatchArgumentWordCount;
			batchArguments[argumentIndex] = drawArguments.indexCountPerInstance;
			batchArguments[argumentIndex + 1] = drawArguments.startIndex;
			batchArguments[argumentIndex + 2] = drawArguments.baseVertexIndex;
			batchArguments[argumentIndex + 3] = 0;
		}

		if (batchCount != 0)
		{
			batchDataBuffer.SetData(batchData, 0, 0, batchCount);
			visibleOffsetsBuffer.SetData(batchVisibleOffsets, 0, 0, batchCount);
			batchArgumentsBuffer.SetData(batchArguments, 0, 0, batchCount * BatchArgumentWordCount);
		}

		layoutDirty = false;
		// Draw v * batchCount + b moves when the batch count changes, and the
		// shadow recordings size their dispatches by it.
		if (layoutBatchCount != batchCount)
		{
			layoutBatchCount = batchCount;
			InvalidateMaterialBindings();
		}
	}

	// Headroom lets streaming and building add instances without forcing a
	// relayout of every batch on each registration.
	private static int GetVisibleRangeCapacity(int registeredInstanceCount)
	{
		if (registeredInstanceCount == 0)
		{
			return 0;
		}

		return checked(registeredInstanceCount + Mathf.Max(
			registeredInstanceCount >> 1,
			MinimumVisibleRangeHeadroom));
	}

	private void ReleaseBatchBuffers()
	{
		ReleaseBuffer(ref batchDataBuffer);
		ReleaseBuffer(ref visibleOffsetsBuffer);
		ReleaseBuffer(ref batchArgumentsBuffer);
		ReleaseBuffer(ref indirectArgumentsBuffer);
		ReleaseBuffer(ref shadowRenderArgumentsBuffer);
		ReleaseBuffer(ref shadowRenderVisibleOffsetsBuffer);
	}

	// The draw reads its instances from the visible range that starts at
	// visibleOffsets[visibleOffsetIndex] + viewBase. Rebinds only after the
	// bindings were invalidated, which a visible stride change does.
	private MaterialPropertyBlock BindInstanceBuffers(
		BoundProperties properties,
		GraphicsBuffer visibleOffsets,
		int visibleOffsetIndex,
		int viewBase)
	{
		var block = properties.Block;
		if (properties.Version == materialBindingsVersion)
		{
			return block;
		}

		block.SetBuffer(InstanceDataId, instanceBuffer);
		block.SetBuffer(InstanceOverridesId, overrideStore.Buffer);
		block.SetBuffer(VisibleInstanceIndicesId, visibleInstanceIndicesBuffer);
		block.SetBuffer(VisibleInstanceOffsetsId, visibleOffsets);
		// SetInt stores a float, which loses view bases past 2^24.
		block.SetInteger(VisibleInstanceOffsetIndexId, visibleOffsetIndex);
		block.SetInteger(VisibleViewBaseId, viewBase);
		properties.Version = materialBindingsVersion;
		return block;
	}

	private void SyncRuntimeBatches()
	{
		while (runtimeBatches.Count < BatchDefinitions.Count)
		{
			AddRuntimeBatch(BatchDefinitions.Get(runtimeBatches.Count));
		}
	}

	private void AddRuntimeBatch(in BatchDefinition definition)
	{
		var runtimeBatch = new RuntimeBatch
		{
			Index = runtimeBatches.Count, SubMeshIndex = definition.SubMeshIndex, Mesh = definition.Mesh,
			MeshBounds = definition.Mesh.bounds, ShadowEligible = definition.HasShadowCasterPass,
			DrawArguments = new GraphicsBuffer.IndirectDrawIndexedArgs
			{
				indexCountPerInstance = definition.Mesh.GetIndexCount(definition.SubMeshIndex),
				startIndex = definition.Mesh.GetIndexStart(definition.SubMeshIndex),
				baseVertexIndex = definition.Mesh.GetBaseVertex(definition.SubMeshIndex),
			},
		};
		var cameraRenderParams = definition.RenderParams;
		cameraRenderParams.shadowCastingMode = ShadowCastingMode.Off;
		runtimeBatch.CameraRenderParams = cameraRenderParams;
		var shadowRenderParams = definition.RenderParams;
		shadowRenderParams.matProps = runtimeBatch.ShadowRenderProperties.Block;
		shadowRenderParams.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
		shadowRenderParams.receiveShadows = false;
		runtimeBatch.ShadowRenderParams = shadowRenderParams;
		runtimeBatches.Add(runtimeBatch);
		transformTracker.SetBatchBounds(runtimeBatch.Index, runtimeBatch.MeshBounds);
		layoutDirty = true;
	}
}
