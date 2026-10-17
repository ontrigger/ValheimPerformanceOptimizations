using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using IndirectRendering;
using UnityEngine;
using VPO = ValheimPerformanceOptimizations.ValheimPerformanceOptimizations;

namespace ValheimPerformanceOptimizations.Patches.IndirectRendering;

/// <summary>
/// Finds piece renderers whose material is changed per object at runtime by
/// vanilla components. Those renderers stay on Unity's path; the rest of the
/// prefab is still batched. Moving renderers are batched: their instances
/// follow their transforms, see IndirectRenderer.Register.
/// </summary>
internal static class DynamicPieceRenderers
{
	private const string SnowOverlayReason = "WearNTear snow overlay reads _SnowLevel";
	private const string FragmentReason = "WearNTear destruction fragments";
	private static readonly StringBuilder Report = new();

	internal static Dictionary<MeshRenderer, string> CollectExcluded(GameObject prefab)
	{
		var excluded = new Dictionary<MeshRenderer, string>();
		foreach (var variation in prefab.GetComponentsInChildren<MaterialVariation>(true))
		{
			Add(excluded, variation.GetComponent<MeshRenderer>(), "MaterialVariation swaps the material at runtime");
		}

		foreach (var wearNTear in prefab.GetComponentsInChildren<WearNTear>(true))
		{
			AddSubtree(excluded, wearNTear.m_snow ? wearNTear.m_snow.gameObject : null, SnowOverlayReason);
			AddSubtree(excluded, wearNTear.m_snowWorn ? wearNTear.m_snowWorn.gameObject : null, SnowOverlayReason);
			AddSubtree(excluded, wearNTear.m_snowBroken ? wearNTear.m_snowBroken.gameObject : null, SnowOverlayReason);
			if (wearNTear.m_fragmentRoots != null)
			{
				// Activated only while the piece is being destroyed, then copied
				// into physics debris.
				foreach (var fragmentRoot in wearNTear.m_fragmentRoots)
				{
					AddSubtree(excluded, fragmentRoot, FragmentReason);
				}
			}
		}

		foreach (var teleport in prefab.GetComponentsInChildren<TeleportWorld>(true))
		{
			Add(excluded, teleport.m_model, "TeleportWorld animates _EmissionColor every frame");
		}

		foreach (var area in prefab.GetComponentsInChildren<PrivateArea>(true))
		{
			Add(excluded, area.m_model, "PrivateArea toggles the _EMISSION keyword");
		}

		foreach (var shield in prefab.GetComponentsInChildren<ShieldGenerator>(true))
		{
			if (shield.m_enabledObject)
			{
				AddAll(
					excluded,
					shield.m_enabledObject.GetComponentsInChildren<MeshRenderer>(true),
					"ShieldGenerator sets per-renderer property blocks");
			}
		}

		foreach (var fader in prefab.GetComponentsInChildren<MaterialFader>(true))
		{
			if (fader.m_renderers != null && fader.m_renderers.Count != 0)
			{
				foreach (var renderer in fader.m_renderers)
				{
					Add(excluded, renderer as MeshRenderer, "MaterialFader animates material properties");
				}
			}
			else
			{
				AddAll(
					excluded,
					fader.GetComponentsInChildren<MeshRenderer>(true),
					"MaterialFader animates material properties");
			}
		}

		foreach (var animator in prefab.GetComponentsInChildren<Animator>(true))
		{
			AddAll(
				excluded,
				animator.GetComponentsInChildren<MeshRenderer>(true),
				$"animated by Animator on '{GetPath(prefab.transform, animator.transform)}'");
		}

		return excluded;
	}

	private static void Add(Dictionary<MeshRenderer, string> excluded, MeshRenderer renderer, string reason)
	{
		if (renderer && !excluded.ContainsKey(renderer))
		{
			excluded.Add(renderer, reason);
		}
	}

	private static void AddSubtree(Dictionary<MeshRenderer, string> excluded, GameObject root, string reason)
	{
		if (root)
		{
			AddAll(excluded, root.GetComponentsInChildren<MeshRenderer>(true), reason);
		}
	}

	private static void AddAll(
		Dictionary<MeshRenderer, string> excluded,
		MeshRenderer[] renderers,
		string reason)
	{
		foreach (var renderer in renderers)
		{
			Add(excluded, renderer, reason);
		}
	}

	/// <summary>
	/// Records per-prefab dynamic material sources and how each renderer ended
	/// up after registration. Call after the prefab's renderers were enabled or
	/// disabled so the reported state is final.
	/// </summary>
	[Conditional("DEBUG")]
	internal static void AppendReport(
		GameObject prefab,
		IReadOnlyDictionary<MeshRenderer, string> excluded,
		bool registered)
	{
		var entry = new StringBuilder();
		var root = prefab.transform;

		foreach (var pair in excluded)
		{
			if (pair.Value != SnowOverlayReason && pair.Value != FragmentReason)
			{
				entry.AppendLine($"    excluded '{GetPath(root, pair.Key.transform)}': {pair.Value}");
			}
		}

		// Low-detail names that end up batched usually mean a LOD level was
		// not covered by any LODGroup and now draws on top of LOD0.
		foreach (var instance in prefab.GetComponentsInChildren<IndirectInstance>(true))
		{
			if (LooksLikeLowDetail(instance.name))
			{
				entry.AppendLine($"    SUSPECT batched low-detail renderer '{GetPath(root, instance.transform)}'");
			}
		}

		foreach (var variation in prefab.GetComponentsInChildren<MaterialVariation>(true))
		{
			var materials = new List<string>();
			foreach (var material in variation.m_materials)
			{
				materials.Add(material.m_material ? material.m_material.name : "<null>");
			}

			entry.AppendLine(
				$"    MaterialVariation '{GetPath(root, variation.transform)}' index={variation.m_materialIndex}: " +
				string.Join(", ", materials));
		}

		foreach (var randomValues in prefab.GetComponentsInChildren<RandomMaterialValues>(true))
		{
			foreach (var property in randomValues.m_vectorProperties)
			{
				entry.AppendLine(
					$"    RandomMaterialValues '{GetPath(root, randomValues.transform)}' " +
					$"properties=[{string.Join(", ", property.m_propertyNames)}] " +
					$"min={property.m_minimum} max={property.m_maximum}");
			}
		}

		foreach (var style in prefab.GetComponentsInChildren<ItemStyle>(true))
		{
			entry.AppendLine($"    ItemStyle '{GetPath(root, style.transform)}'");
		}

		var wearNTear = prefab.GetComponent<WearNTear>();
		if (wearNTear)
		{
			AppendWearObject(entry, root, "m_new", wearNTear.m_new);
			AppendWearObject(entry, root, "m_worn", wearNTear.m_worn);
			AppendWearObject(entry, root, "m_broken", wearNTear.m_broken);
			AppendWearObject(entry, root, "m_wet", wearNTear.m_wet);
			AppendWearObject(entry, root, "m_snow", wearNTear.m_snow ? wearNTear.m_snow.gameObject : null);
			AppendWearObject(entry, root, "m_snowWorn", wearNTear.m_snowWorn ? wearNTear.m_snowWorn.gameObject : null);
			AppendWearObject(
				entry,
				root,
				"m_snowBroken",
				wearNTear.m_snowBroken ? wearNTear.m_snowBroken.gameObject : null);
		}

		if (entry.Length == 0)
		{
			return;
		}

		Report.AppendLine($"  {prefab.name} (registered={registered})");
		Report.Append(entry);
	}

	// Whole-word "low"/"lod" (but not "lod0"), so names like "bellow" or
	// "logs_glowing" do not match.
	private static bool LooksLikeLowDetail(string name)
	{
		var tokens = name.ToLowerInvariant().Split(' ', '_', '.', '(', ')', '[', ']');
		foreach (var token in tokens)
		{
			if (token == "low" || token == "lod" || (token.StartsWith("lod") && token != "lod0"))
			{
				return true;
			}
		}

		return false;
	}

	private static void AppendWearObject(StringBuilder entry, Transform root, string field, GameObject gameObject)
	{
		if (!gameObject)
		{
			return;
		}

		var states = new List<string>();
		foreach (var renderer in gameObject.GetComponentsInChildren<MeshRenderer>(true))
		{
			var state = renderer.GetComponent<IndirectInstance>()
				? "batched"
				: renderer.enabled
					? "native"
					: "DISABLED";
			states.Add($"{GetPath(root, renderer.transform)}={state}");
		}

		entry.AppendLine(
			$"    WearNTear.{field} '{GetPath(root, gameObject.transform)}' " +
			$"active={gameObject.activeSelf} renderers=[{string.Join(", ", states)}]");
	}

	[Conditional("DEBUG")]
	internal static void FlushReport()
	{
		if (Report.Length == 0)
		{
			return;
		}

		VPO.Logger.LogInfo("IndirectRenderer dynamic material report:\n" + Report);
		Report.Clear();
	}

	private static string GetPath(Transform root, Transform transform)
	{
		if (transform == root)
		{
			return ".";
		}

		var path = transform.name;
		for (var parent = transform.parent; parent && parent != root; parent = parent.parent)
		{
			path = parent.name + "/" + path;
		}

		return path;
	}
}
