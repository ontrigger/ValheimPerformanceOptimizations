using System.Collections.Generic;
using BepInEx.Logging;
using IndirectRendering;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches.IndirectRendering;

internal sealed class VPOBatchedPrefab : MonoBehaviour
{
	private static readonly int ColorId = Shader.PropertyToID("_Color");
	private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");
	private static readonly int UVOffsetId = Shader.PropertyToID("_UVOffset");
	private static readonly int TakingAshlandsDamageId = Shader.PropertyToID("_TakingAshlandsDamage");
	private static readonly int SnowLevelId = Shader.PropertyToID("_SnowLevel");
	private static readonly HashSet<VPOBatchedPrefab> Live = new();

	public IndirectInstance[] instances;
	public MeshRenderer[] disabledRenderers;
	// This piece's root MaterialMan block; see IndirectRendererPatches.
	internal MaterialMan.PropertyContainer MaterialContainer;

	private bool fallback;
	// An unsupported property in the root block; cleared when it is reset.
	private bool propertyFallback;
	// A MaterialMan block on a child cannot be expressed per piece; sticky.
	private bool childBlockFallback;
	// Placement ghosts and objects without a valid ZDO are drawn by Unity.
	private bool viewFallback;
	private int overrideIndex;
	private VPOIndirectInstanceOverride @override;

	internal int OverrideIndex => overrideIndex;

	internal void ApplyMaterialProperties(Dictionary<int, MaterialMan.ShaderPropertyBase> properties)
	{
		var data = default(VPOIndirectInstanceOverride);
		var needsFallback = false;
		foreach (var pair in properties)
		{
			if (pair.Key == ColorId && TryGetVector(pair.Value, out var color))
			{
				data.Color = color;
				data.Flags |= VPOIndirectInstanceOverride.HasColor;
			}
			else if (pair.Key == EmissionColorId && TryGetVector(pair.Value, out var emissionColor))
			{
				data.EmissionColor = emissionColor;
				data.Flags |= VPOIndirectInstanceOverride.HasEmissionColor;
			}
			else if (pair.Key == UVOffsetId && TryGetVector(pair.Value, out var uvOffset))
			{
				data.UVOffset = uvOffset;
				data.Flags |= VPOIndirectInstanceOverride.HasUVOffset;
			}
			else if (pair.Key == TakingAshlandsDamageId &&
			         pair.Value is MaterialMan.ShaderProperty<float> takingAshlandsDamage)
			{
				if (takingAshlandsDamage.Get() > 0.5f)
				{
					data.TakingAshlandsDamage = 1f;
					data.Flags |= VPOIndirectInstanceOverride.HasTakingAshlandsDamage;
				}
			}
			else if (pair.Key != SnowLevelId)
			{
				needsFallback = true;
				IndirectRendererPatches.LogDiagnostic(
					LogLevel.Info,
					$"IndirectRenderer: '{Utils.GetPrefabName(gameObject)}' falls back to Unity rendering for unsupported MaterialMan "
					+
					$"property id {pair.Key}.");
			}
		}

		propertyFallback = needsFallback;
		UpdateRenderingPath();
		SetOverride(data);
	}

	internal void ApplyChildBlockProperties(Dictionary<int, MaterialMan.ShaderPropertyBase> properties)
	{
		if (childBlockFallback) { return; }

		foreach (var propertyId in properties.Keys)
		{
			if (!IsReadByBatchedMaterial(propertyId)) { continue; }

			IndirectRendererPatches.LogDiagnostic(
				LogLevel.Info,
				$"IndirectRenderer: '{Utils.GetPrefabName(gameObject)}' falls back to Unity rendering for " +
				$"MaterialMan property id {propertyId} on a child.");
			childBlockFallback = true;
			UpdateRenderingPath();
			return;
		}
	}

	private bool IsReadByBatchedMaterial(int propertyId)
	{
		foreach (var instance in instances)
		{
			foreach (var material in instance.GetComponent<MeshRenderer>().sharedMaterials)
			{
				if (material && material.HasProperty(propertyId))
				{
					return true;
				}
			}
		}

		return false;
	}

	private void Awake()
	{
		if (instances != null)
		{
			Track();
		}
	}

	// Before the instances' Start, so they never register with an invalid
	// view. ZNetView.Awake runs first: its execution order is -20.
	internal void Track()
	{
		Live.Add(this);
		viewFallback = !TryGetComponent<ZNetView>(out var view) || !view.IsValid();
		UpdateRenderingPath();
	}

	internal static void ApplyEnabledToAll()
	{
		foreach (var prefab in Live)
		{
			if (prefab)
			{
				prefab.UpdateRenderingPath();
			}
		}
	}

	private void UpdateRenderingPath()
	{
		if (propertyFallback || childBlockFallback || viewFallback || !IndirectRendererPatches.Enabled.Value)
		{
			FallbackToUnityRendering();
		}
		else
		{
			ResumeIndirectRendering();
		}
	}

	private void SetOverride(in VPOIndirectInstanceOverride data)
	{
		var indirectRenderer = VPOIndirectRenderer.Renderer;
		if (indirectRenderer == null || (overrideIndex != 0 && data.Equals(@override)))
		{
			return;
		}

		@override = data;
		var previousIndex = overrideIndex;
		if (data.Flags == 0)
		{
			if (previousIndex == 0) { return; }

			overrideIndex = 0;
			indirectRenderer.ReleaseOverride(previousIndex);
		}
		else
		{
			if (previousIndex == 0)
			{
				overrideIndex = indirectRenderer.AllocateOverride();
			}

			indirectRenderer.UpdateOverride(overrideIndex, data);
		}

		if (overrideIndex != previousIndex)
		{
			foreach (var instance in instances)
			{
				instance.SetOverrideIndex(overrideIndex);
			}
		}
	}

	private void FallbackToUnityRendering()
	{
		if (fallback) { return; }

		fallback = true;
		foreach (var instance in instances)
		{
			instance.enabled = false;
		}

		foreach (var renderer in disabledRenderers)
		{
			renderer.enabled = true;
		}
	}

	private void ResumeIndirectRendering()
	{
		if (!fallback) { return; }

		fallback = false;
		foreach (var renderer in disabledRenderers)
		{
			renderer.enabled = false;
		}

		foreach (var instance in instances)
		{
			instance.enabled = true;
		}
	}

	private void OnDestroy()
	{
		Live.Remove(this);
		// The container map is cleaned up by MaterialManNotifier.OnDestroy on
		// this same GameObject, via the UnregisterRenderers prefix.
		if (overrideIndex != 0)
		{
			VPOIndirectRenderer.Renderer?.ReleaseOverride(overrideIndex);
			overrideIndex = 0;
		}
	}

	private static bool TryGetVector(MaterialMan.ShaderPropertyBase property, out Vector4 value)
	{
		if (property is MaterialMan.ShaderProperty<Color> color)
		{
			value = color.Get();
			return true;
		}
		if (property is MaterialMan.ShaderProperty<Vector4> vector)
		{
			value = vector.Get();
			return true;
		}
		if (property is MaterialMan.ShaderProperty<Vector3> vector3)
		{
			value = vector3.Get();
			return true;
		}
		if (property is MaterialMan.ShaderProperty<Vector2> vector2)
		{
			value = vector2.Get();
			return true;
		}
		value = default;
		return false;
	}
}
