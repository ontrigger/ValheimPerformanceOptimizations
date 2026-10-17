using System;
using System.Collections.Generic;
using IndirectRendering;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches.IndirectRendering;

/// <summary>
/// Gives every batched LODGroup renderer the camera distances its levels are
/// drawn at, matching Unity's selection: level i is drawn while the group's
/// world size over the distance to its reference point, scaled by
/// lodBias / (2 tan(fov / 2)), is at least its transition height. Distances
/// are in prefab root space for a scale of 1; every level of a group measures
/// from the same point, so levels switch together without pairing.
/// </summary>
internal static class LodRanges
{
	internal static void Assign(GameObject prefab)
	{
		var root = prefab.transform;
		var rootScale = LodRange.MaxAbs(root.lossyScale);
		var assigned = new HashSet<IndirectInstance>();
		foreach (var lodGroup in prefab.GetComponentsInChildren<LODGroup>(true))
		{
			if (!lodGroup.enabled)
			{
				continue;
			}

			var groupTransform = lodGroup.transform;
			var referencePoint = root.InverseTransformPoint(
				groupTransform.TransformPoint(lodGroup.localReferencePoint));
			var size = lodGroup.size * LodRange.MaxAbs(groupTransform.lossyScale) / rootScale;
			var startDistance = 0f;
			assigned.Clear();
			foreach (var lod in lodGroup.GetLODs())
			{
				var endDistance = size / lod.screenRelativeTransitionHeight;
				foreach (var renderer in lod.renderers)
				{
					if (!renderer || !renderer.TryGetComponent<IndirectInstance>(out var instance))
					{
						continue;
					}

					// a renderer in several levels spans them all.
					if (assigned.Add(instance))
					{
						instance.lodReferencePoint = referencePoint;
						instance.lodStartDistance = startDistance;
					}

					instance.lodEndDistance = endDistance;
				}

				startDistance = endDistance;
			}
		}
	}
}
