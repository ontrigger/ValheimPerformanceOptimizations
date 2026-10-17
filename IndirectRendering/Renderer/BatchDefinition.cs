using System;
using System.Collections.Generic;
using UnityEngine;

namespace IndirectRendering;

/// <summary>
/// One submesh drawn with one material and one set of render settings.
/// Renderers whose definitions are equal share a batch.
/// </summary>
public struct BatchDefinition : IEquatable<BatchDefinition>
{
	public Mesh Mesh;
	public int SubMeshIndex;
	// Derived from the material and RenderParams, so equality ignores it.
	public bool HasShadowCasterPass;
	public RenderParams RenderParams;

	public readonly bool Equals(BatchDefinition other)
	{
		return Mesh.GetInstanceID() == other.Mesh.GetInstanceID() &&
			SubMeshIndex == other.SubMeshIndex &&
			RenderParams.material.GetInstanceID() == other.RenderParams.material.GetInstanceID() &&
			RenderParams.shadowCastingMode == other.RenderParams.shadowCastingMode &&
			RenderParams.receiveShadows == other.RenderParams.receiveShadows &&
			RenderParams.layer == other.RenderParams.layer &&
			RenderParams.renderingLayerMask == other.RenderParams.renderingLayerMask;
	}

	public override readonly bool Equals(object obj)
	{
		return obj is BatchDefinition other && Equals(other);
	}

	public override readonly int GetHashCode()
	{
		return HashCode.Combine(
			Mesh.GetInstanceID(),
			SubMeshIndex,
			RenderParams.material.GetInstanceID(),
			RenderParams.shadowCastingMode,
			RenderParams.receiveShadows,
			RenderParams.layer,
			RenderParams.renderingLayerMask);
	}
}
/// <summary>
/// Every batch definition, indexed by batch id. Hosts may keep batch ids
/// across scene reloads, so this outlives the IndirectRenderer, whose batch i
/// is always definition i.
/// </summary>
public static class BatchDefinitions
{
	private static readonly List<BatchDefinition> Definitions = new();
	private static readonly Dictionary<BatchDefinition, int> IdByDefinition = new();

	public static int Count => Definitions.Count;

	public static BatchDefinition Get(int batchId)
	{
		return Definitions[batchId];
	}

	public static bool GetOrAdd(in BatchDefinition definition, out int batchId)
	{
		if (IdByDefinition.TryGetValue(definition, out batchId))
		{
			return false;
		}

		batchId = Definitions.Count;
		Definitions.Add(definition);
		IdByDefinition.Add(definition, batchId);
		return true;
	}
}
