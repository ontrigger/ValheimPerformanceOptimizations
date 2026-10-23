using System.Collections.Generic;
using System.Runtime.InteropServices;
using IndirectRendering.Extensions;
using UnityEngine;

namespace IndirectRendering;

/// <summary>
/// The shadow casting batches that the resolver gives a caster material, with
/// triangle meshes that have normals, grouped by caster material so each
/// group draws through one native multi-draw per light; see MultiDraw and
/// CasterGeometry.
/// </summary>
internal sealed class MultiDrawCasterSet
{
	// Must match PieceFrustumCull.compute's MultiDrawBatch.
	[StructLayout(LayoutKind.Sequential)]
	internal struct Batch
	{
		internal uint BatchIndex;
		internal uint IndexCount;
		internal uint StartIndex;
		internal uint LayerMask;
	}

	// The batches whose casters share a caster material.
	internal sealed class Group
	{
		internal Material Material;
		internal readonly List<Batch> Batches = new();
		// Where Batches start in BatchBuffer.
		internal int FirstBatch;
	}

	private readonly MultiDrawCasterResolver resolver;
	private readonly CasterGeometryBuffer geometryBuffer = new();
	private readonly List<Group> groups = new();
	private readonly List<Batch> batchesScratch = new();
	// Indexed by batch: the resolver's batch parameters, read by the caster
	// shader through the batch index its instances carry.
	private readonly List<Vector4> batchParameters = new();
	// Batches below this index were checked.
	private int scannedBatchCount;
	private bool dirty;
	private GraphicsBuffer batchParameterBuffer;
	private GraphicsBuffer batchBuffer;

	internal MultiDrawCasterSet(MultiDrawCasterResolver resolver)
	{
		this.resolver = resolver;
	}

	internal bool HasResolver => resolver != null;
	internal CasterGeometryBuffer GeometryBuffer => geometryBuffer;
	internal IReadOnlyList<Group> Groups => groups;
	// The batches of every group, from the last TryUpload.
	internal int BatchCount { get; private set; }
	internal GraphicsBuffer BatchBuffer => batchBuffer;
	internal GraphicsBuffer BatchParameterBuffer => batchParameterBuffer;

	/// <summary>
	/// Checks the batches added since the last scan, and sets MultiDrawCaster
	/// on the ones it adds. Returns the lowest batch index it added, or -1.
	/// </summary>
	internal int Scan(List<IndirectRenderer.RuntimeBatch> runtimeBatches)
	{
		var firstAdded = -1;
		for (; scannedBatchCount < runtimeBatches.Count; scannedBatchCount += 1)
		{
			var runtimeBatch = runtimeBatches[scannedBatchCount];
			if (!runtimeBatch.ShadowEligible ||
			    !resolver(runtimeBatch.ShadowRenderParams.material, out var casterMaterial, out var parameters) ||
			    !casterMaterial ||
			    !geometryBuffer.TryGetIndexRange(runtimeBatch.Mesh, runtimeBatch.SubMeshIndex, out var indexRange))
			{
				continue;
			}

			var group = groups.Find(candidate => candidate.Material == casterMaterial);
			if (group == null)
			{
				group = new Group { Material = casterMaterial };
				groups.Add(group);
			}

			while (batchParameters.Count <= runtimeBatch.Index)
			{
				batchParameters.Add(Vector4.zero);
			}

			batchParameters[runtimeBatch.Index] = parameters;

			group.Batches.Add(new Batch
			{
				BatchIndex = (uint)runtimeBatch.Index,
				IndexCount = (uint)indexRange.IndexCount,
				StartIndex = (uint)indexRange.StartIndex,
				LayerMask = 1u << runtimeBatch.ShadowRenderParams.layer,
			});
			runtimeBatch.MultiDrawCaster = true;
			if (firstAdded < 0)
			{
				firstAdded = runtimeBatch.Index;
			}

			dirty = true;
		}

		return firstAdded;
	}

	/// <summary>
	/// Rebuilds every buffer if a scan added batches; they are added rarely
	/// after loading. Returns whether it did, which moves the index buffer and
	/// each group's draw range.
	/// </summary>
	internal bool TryUpload()
	{
		if (!dirty) { return false; }

		dirty = false;
		geometryBuffer.Upload();
		GraphicsBufferX.Release(ref batchParameterBuffer);
		GraphicsBufferX.Release(ref batchBuffer);
		batchParameterBuffer = new GraphicsBuffer(
			GraphicsBuffer.Target.Structured,
			batchParameters.Count,
			sizeof(float) * 4);
		batchParameterBuffer.SetData(batchParameters);

		var batches = batchesScratch;
		batches.Clear();
		foreach (var group in groups)
		{
			group.FirstBatch = batches.Count;
			batches.AddRange(group.Batches);
		}

		BatchCount = batches.Count;
		batchBuffer = new GraphicsBuffer(
			GraphicsBuffer.Target.Structured,
			BatchCount,
			Marshal.SizeOf<Batch>());
		batchBuffer.SetData(batches);
		return true;
	}

	internal void Release()
	{
		geometryBuffer.Release();
		GraphicsBufferX.Release(ref batchParameterBuffer);
		GraphicsBufferX.Release(ref batchBuffer);
		groups.Clear();
		scannedBatchCount = 0;
		BatchCount = 0;
		dirty = false;
		batchParameters.Clear();
	}
}
