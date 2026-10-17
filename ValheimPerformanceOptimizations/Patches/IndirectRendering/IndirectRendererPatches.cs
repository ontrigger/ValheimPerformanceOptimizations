using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using IndirectRendering;
using UnityEngine;
using UnityEngine.Rendering;
using VPO = ValheimPerformanceOptimizations.ValheimPerformanceOptimizations;

namespace ValheimPerformanceOptimizations.Patches.IndirectRendering;

[HarmonyPatch]
internal static class IndirectRendererPatches
{
	private static readonly HashSet<int> RegisteredPrefabHashes = new();

	private static ConfigEntry<KeyboardShortcut> _toggleShortcut;
	internal static ConfigEntry<bool> Enabled { get; private set; }
	internal static ConfigEntry<float> MinimumScreenPercentage { get; private set; }

	private static bool _initialized;

	static IndirectRendererPatches()
	{
		VPO.OnInitialized += Initialize;
	}

	private static void Initialize(ConfigFile configFile, Harmony harmony)
	{
		const string section = "Indirect Renderer";
		Enabled = configFile.Bind(
			section,
			"Enabled",
			true,
			"Draw geometry with the indirect renderer. When off, they are drawn by Unity.");
		_toggleShortcut = configFile.Bind(
			section,
			"Toggle shortcut",
			new KeyboardShortcut(KeyCode.F8, KeyCode.LeftAlt),
			"Toggles Enabled in game, for comparing against vanilla rendering.");
		MinimumScreenPercentage = configFile.Bind(
			section,
			"Minimum screen percentage",
			0.1f,
			"Skip indirect instances whose projected bounds span less than this percentage of both screen dimensions. Set to 0 to disable.");
		Enabled.SettingChanged += (_, _) => ApplyEnabled();
		MinimumScreenPercentage.SettingChanged += (_, _) =>
			VPOIndirectRenderer.Renderer?.SetMinimumScreenFraction(MinimumScreenPercentage.Value * 0.01f);
	}

	// Why prefabs, renderers and objects stay on Unity's path. Release builds
	// drop these calls, including formatting their messages.
	[Conditional("DEBUG")]
	internal static void LogDiagnostic(LogLevel level, string message)
	{
		VPO.Logger.Log(level, message);
	}

	private static void ApplyEnabled()
	{
		VPOIndirectRenderer.Renderer?.SetEnabled(Enabled.Value);

		VPOBatchedPrefab.ApplyEnabledToAll();
	}

	[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Update))]
	[HarmonyPostfix]
	private static void ZNetSceneUpdatePostfix()
	{
		if (_toggleShortcut.Value.IsDown())
		{
			Enabled.Value = !Enabled.Value;
			ShowMessage(Enabled.Value ? "VPO indirect rendering: on" : "VPO indirect rendering: off (vanilla)");
		}
	}

	private static void ShowMessage(string message)
	{
		if (MessageHud.instance)
		{
			MessageHud.instance.ShowMessage(MessageHud.MessageType.TopLeft, message);
		}
	}

	// Shutdown destroys every networked object before ZNetScene goes.
	[HarmonyPatch(typeof(Game), nameof(Game.Shutdown))]
	[HarmonyPrefix]
	private static void GameShutdownPrefix()
	{
		VPOIndirectRenderer.Renderer?.CloseRegistrations();
	}

	[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
	[HarmonyPriority(Priority.First)]
	[HarmonyPostfix]
	private static void ZNetSceneAwakePostfix(ZNetScene __instance)
	{
		if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
		{
			return;
		}

		PieceShaderBundle.Load();
		if (!__instance.TryGetComponent<VPOIndirectRenderer>(out _))
		{
			__instance.gameObject.AddComponent<VPOIndirectRenderer>();
		}

		VPOIndirectRenderer.Renderer.ResetScene();
		VPOIndirectRenderer.Renderer.SetEnabled(Enabled.Value);

		// The patched prefabs and their batch definitions outlive the ZNetScene.
		if (_initialized)
		{
			return;
		}

		foreach (var prefab in __instance.m_prefabs)
		{
			if (!prefab)
			{
				continue;
			}

			if (!TryGetRegistrationKind(prefab, out var isPiece))
			{
				continue;
			}

			var excludedRenderers = DynamicPieceRenderers.CollectExcluded(prefab);
			// Destructibles include vegetation and props with shaders we have no
			// indirect version of; skipping those is expected, not worth a warning.
			var registered = TryRegisterPrefab(
				prefab,
				excludedRenderers,
				isPiece,
				out var failureReason);
			if (registered)
			{
				RegisteredPrefabHashes.Add(prefab.name.GetStableHashCode());
			}
			else if (isPiece)
			{
				LogDiagnostic(
					LogLevel.Warning,
					$"IndirectRenderer skipped prefab '{prefab.name}': {failureReason}");
			}
			else
			{
				LogDiagnostic(
					LogLevel.Debug,
					$"IndirectRenderer skipped destructible prefab '{prefab.name}': {failureReason}");
			}

			DynamicPieceRenderers.AppendReport(prefab, excludedRenderers, registered);
		}

		DynamicPieceRenderers.FlushReport();
		_initialized = true;
	}

	private static bool TryGetRegistrationKind(GameObject gameObject, out bool isPiece)
	{
		isPiece = gameObject.TryGetComponent<Piece>(out _);
		// Tree shake and regrowth animate the trunk; instances follow it.
		// ItemDrop prefabs are registered per spawned object instead; see
		// TryRegisterItemDrop.
		return isPiece || gameObject.TryGetComponent<Destructible>(out _) ||
			gameObject.TryGetComponent<TreeBase>(out _) ||
			(gameObject.TryGetComponent<Pickable>(out _) && !gameObject.TryGetComponent<ItemDrop>(out _));
	}

	// Item prefabs are also the source of equipped and displayed items:
	// VisEquipment, ItemStand, ArmorStand and CookingStation instantiate their
	// "attach" child. Patching the prefab would carry disabled renderers into
	// those copies, so each dropped item is registered when it spawns.
	private static readonly Dictionary<int, bool> RegistrableByPrefabHash = new();

	private static void TryRegisterItemDrop(GameObject gameObject, int prefabHash)
	{
		var known = RegistrableByPrefabHash.TryGetValue(prefabHash, out var registrable);
		if (known && !registrable)
		{
			return;
		}

		registrable = TryRegisterPrefab(
			gameObject,
			DynamicPieceRenderers.CollectExcluded(gameObject),
			false,
			out var failureReason);
		if (known)
		{
			return;
		}

		RegistrableByPrefabHash.Add(prefabHash, registrable);
		if (!registrable)
		{
			LogDiagnostic(
				LogLevel.Debug,
				$"IndirectRenderer skipped item '{Utils.GetPrefabName(gameObject)}': {failureReason}");
		}
	}

	// Locations and dungeon rooms instantiate the piece copies baked into their
	// own prefabs, not the ZNetScene prefabs patched above, so those copies
	// are registered when they spawn. They switch to the patched prefab only
	// after their zone reloads from the ZDO.
	// Awake already stored the prefab hash in the ZDO, so look it up there
	// instead of calling Utils.GetPrefabName, which allocates.
	[HarmonyPatch(typeof(ZNetView), nameof(ZNetView.Awake))]
	[HarmonyPostfix]
	private static void ZNetViewAwakePostfix(ZNetView __instance)
	{
		if (!_initialized ||
		    VPOIndirectRenderer.Renderer == null ||
		    __instance.m_ghost ||
		    __instance.m_zdo == null)
		{
			return;
		}

		if (__instance.TryGetComponent<ItemDrop>(out _))
		{
			TryRegisterItemDrop(__instance.gameObject, __instance.m_zdo.GetPrefab());
			return;
		}

		if (!RegisteredPrefabHashes.Contains(__instance.m_zdo.GetPrefab()) ||
		    __instance.TryGetComponent<VPOBatchedPrefab>(out _) ||
		    !TryGetRegistrationKind(__instance.gameObject, out _))
		{
			return;
		}

		var gameObject = __instance.gameObject;

		TryRegisterPrefab(
			gameObject,
			DynamicPieceRenderers.CollectExcluded(gameObject),
			false,
			out _);
	}

	private static void ReplaceCompatibleMaterials(MeshRenderer renderer)
	{
		var materials = renderer.sharedMaterials;
		var changed = false;
		for (var index = 0; index < materials.Length; index += 1)
		{
			if (!PieceMaterialReplacer.TryCreate(materials[index], out var replacement))
			{
				continue;
			}

			materials[index] = replacement;
			changed = true;
		}

		if (changed)
		{
			renderer.sharedMaterials = materials;
		}
	}

	private static bool TryRegisterPrefab(
		GameObject prefab,
		IReadOnlyDictionary<MeshRenderer, string> excludedRenderers,
		bool logSkippedRenderers,
		out string failureReason)
	{
		failureReason = null;
		var renderers = FindBatchRenderers(prefab, excludedRenderers);
		if (renderers.Count == 0)
		{
			failureReason = excludedRenderers.Count != 0
				? "every eligible renderer changes dynamically at runtime"
				: "no eligible MeshRenderer was found";
			return false;
		}

		var skippedRenderers = new HashSet<MeshRenderer>(excludedRenderers.Keys);
		var instances = new List<IndirectInstance>(renderers.Count);
		var definitions = new List<BatchDefinition>();
		foreach (var batchRenderer in renderers)
		{
			var originalMaterials = batchRenderer.sharedMaterials;
			ReplaceCompatibleMaterials(batchRenderer);
			definitions.Clear();
			if (!TryCreateDefinitions(batchRenderer, definitions, out var rendererFailureReason))
			{
				batchRenderer.sharedMaterials = originalMaterials;
				skippedRenderers.Add(batchRenderer);
				if (logSkippedRenderers)
				{
					LogDiagnostic(
						LogLevel.Warning,
						$"IndirectRenderer skipped renderer '{batchRenderer.name}' on prefab '{prefab.name}': " +
						$"{rendererFailureReason}");
				}

				continue;
			}

			var batchIds = new int[definitions.Count];
			for (var i = 0; i < definitions.Count; i += 1)
			{
				batchIds[i] = GetOrCreateBatchId(definitions[i]);
			}

			var instance = batchRenderer.gameObject.AddComponent<IndirectInstance>();
			instance.batchIds = batchIds;
			instance.lodRoot = prefab.transform;
			instances.Add(instance);
		}

		if (instances.Count == 0)
		{
			failureReason = "no compatible renderer was found";
			return false;
		}

		// Batched levels are drawn in their range on the GPU; skipped ones stay
		// under their LODGroup, which selects levels by the same distances.
		LodRanges.Assign(prefab);

		// Renderers vanilla already disabled are left out so fallback never
		// turns them on.
		var disabledRenderers = new List<MeshRenderer>();
		foreach (var childRenderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
		{
			if (childRenderer.enabled && !skippedRenderers.Contains(childRenderer))
			{
				childRenderer.enabled = false;
				disabledRenderers.Add(childRenderer);
			}
		}

		var pieceRoot = prefab.AddComponent<VPOBatchedPrefab>();
		pieceRoot.instances = instances.ToArray();
		pieceRoot.disabledRenderers = disabledRenderers.ToArray();

		// Location copies are already in the scene; copies of prefab assets
		// are tracked from VPOBatchedPrefab.Awake.
		if (prefab.scene.IsValid())
		{
			pieceRoot.Track();
		}

		return true;
	}

	private static bool TryCreateDefinitions(
		MeshRenderer batchRenderer,
		List<BatchDefinition> definitions,
		out string failureReason)
	{
		failureReason = null;
		var rendererName = batchRenderer.name;
		var meshFilter = batchRenderer.GetComponent<MeshFilter>();
		if (!meshFilter)
		{
			failureReason = $"renderer '{rendererName}' has no MeshFilter";
			return false;
		}

		var mesh = meshFilter.sharedMesh;
		if (!mesh)
		{
			failureReason = $"renderer '{rendererName}' has no mesh";
			return false;
		}

		if (mesh.subMeshCount == 0)
		{
			failureReason = $"renderer '{rendererName}' has a mesh with no submeshes";
			return false;
		}

		var materials = batchRenderer.sharedMaterials;
		if (materials.Length != mesh.subMeshCount)
		{
			failureReason = $"renderer '{rendererName}' has {materials.Length} materials but its mesh has " +
				$"{mesh.subMeshCount} submeshes";
			return false;
		}

		for (var materialIndex = 0; materialIndex < materials.Length; materialIndex += 1)
		{
			var material = materials[materialIndex];
			if (!material)
			{
				failureReason = $"renderer '{rendererName}' has no material at index {materialIndex}";
				return false;
			}

			if (!PieceMaterialReplacer.IsCompatible(material))
			{
				failureReason = $"renderer '{rendererName}' uses an incompatible material " +
					$"'{material.name}' ({(material.shader ? material.shader.name : "no shader")}) at index {materialIndex}";
				return false;
			}
		}

		for (var subMeshIndex = 0; subMeshIndex < materials.Length; subMeshIndex += 1)
		{
			var renderParams = new RenderParams(materials[subMeshIndex])
			{
				shadowCastingMode = batchRenderer.shadowCastingMode, receiveShadows = batchRenderer.receiveShadows,
				layer = batchRenderer.gameObject.layer, renderingLayerMask = batchRenderer.renderingLayerMask,
			};

			definitions.Add(new BatchDefinition
			{
				Mesh = mesh, SubMeshIndex = subMeshIndex, HasShadowCasterPass
					= batchRenderer.shadowCastingMode != ShadowCastingMode.Off &&
					materials[subMeshIndex].FindPass("ShadowCaster") >= 0,
				RenderParams = renderParams,
			});
		}

		return true;
	}

	private static int GetOrCreateBatchId(in BatchDefinition definition)
	{
		if (BatchDefinitions.GetOrAdd(definition, out var batchId) &&
		    definition.RenderParams.shadowCastingMode != ShadowCastingMode.Off &&
		    !definition.HasShadowCasterPass)
		{
			LogDiagnostic(
				LogLevel.Warning,
				$"IndirectRenderer definition for mesh '{definition.Mesh.name}', submesh {definition.SubMeshIndex}, " +
				$"material '{definition.RenderParams.material.name}' has no ShadowCaster pass and will be skipped " +
				"for directional shadows.");
		}

		return batchId;
	}

	/// <summary>
	/// Every renderer vanilla would draw, including those under currently
	/// inactive objects (worn/broken variants, feast levels, chest lids) and
	/// every LOD level. Registration then follows each object's activation.
	/// Renderers vanilla keeps disabled (combined-mesh sources) are left out.
	/// </summary>
	private static List<MeshRenderer> FindBatchRenderers(
		GameObject prefab,
		IReadOnlyDictionary<MeshRenderer, string> excludedRenderers)
	{
		var renderers = new List<MeshRenderer>();
		foreach (var renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
		{
			if (renderer.enabled &&
			    !excludedRenderers.ContainsKey(renderer) &&
			    !IsSupersededCombinedMesh(renderer))
			{
				renderers.Add(renderer);
			}
		}

		return renderers;
	}

	// Some prefabs carry both combined LODs without a LODGroup entry for the
	// low one; drawing both would overlap.
	internal static bool IsSupersededCombinedMesh(MeshRenderer renderer)
	{
		var parent = renderer.transform.parent;
		if (!parent || !renderer.name.Contains("_Combined Mesh [low]"))
		{
			return false;
		}

		foreach (Transform sibling in parent)
		{
			if (sibling.name.Contains("_Combined Mesh [high]"))
			{
				return true;
			}
		}

		return false;
	}

	// Null for containers that are not a batched prefab's root block.
	private static readonly Dictionary<MaterialMan.PropertyContainer, VPOBatchedPrefab> PrefabByPropContainer = new();
	// Blocks on a child of a batched prefab, such as VisEquipment's _SnowCover on
	// an armor stand; see VPOBatchedPrefab.ApplyChildBlockProperties.
	private static readonly Dictionary<MaterialMan.PropertyContainer, VPOBatchedPrefab> PrefabByChildContainer = new();

	[HarmonyPatch(typeof(MaterialMan), nameof(MaterialMan.RegisterRenderers))]
	[HarmonyPostfix]
	private static void MaterialManRegisterRenderersPostfix(MaterialMan __instance, GameObject gameObject)
	{
		var container = __instance.m_blocks[gameObject.GetInstanceID()];
		if (PrefabByPropContainer.ContainsKey(container)) { return; }

		var root = gameObject.GetComponentInParent<VPOBatchedPrefab>();
		if (!root)
		{
			PrefabByPropContainer.Add(container, null);
			return;
		}

		if (!root.TryGetComponent<ZNetView>(out var view) || view.GetZDO() == null)
		{
			return;
		}

		if (root.gameObject != gameObject)
		{
			// Its properties decide whether the prefab falls back, once set.
			PrefabByPropContainer.Add(container, null);
			PrefabByChildContainer[container] = root;
			return;
		}

		if (root.MaterialContainer != null)
		{
			PrefabByPropContainer.Remove(root.MaterialContainer);
		}

		root.MaterialContainer = container;
		PrefabByPropContainer.Add(container, root);
	}

	[HarmonyPatch(typeof(MaterialMan), nameof(MaterialMan.UnregisterRenderers))]
	[HarmonyPrefix]
	private static void MaterialManUnregisterRenderersPrefix(MaterialMan __instance, GameObject gameObject)
	{
		if (!__instance.m_blocks.TryGetValue(gameObject.GetInstanceID(), out var container))
		{
			return;
		}

		PrefabByChildContainer.Remove(container);
		if (PrefabByPropContainer.Remove(container, out var root) && root != null)
		{
			root.MaterialContainer = null;
		}
	}

	[HarmonyPatch(typeof(MaterialMan.PropertyContainer), nameof(MaterialMan.PropertyContainer.UpdateBlock))]
	[HarmonyPostfix]
	private static void PropertyContainerUpdateBlockPostfix(MaterialMan.PropertyContainer __instance)
	{
		if (PrefabByPropContainer.TryGetValue(__instance, out var prefab) && prefab)
		{
			prefab.ApplyMaterialProperties(__instance.m_shaderProperties);
		}
		else if (PrefabByChildContainer.TryGetValue(__instance, out var childPrefab) && childPrefab)
		{
			childPrefab.ApplyChildBlockProperties(__instance.m_shaderProperties);
		}
	}

	private static readonly MethodInfo RendererIsVisibleGetter =
		AccessTools.PropertyGetter(typeof(Renderer), nameof(Renderer.isVisible));

	// Vanilla spawns fragments only for visible renderers, and batched renderers are disabled.
	[HarmonyPatch(typeof(Destructible), nameof(Destructible.CreateFragments))]
	[HarmonyTranspiler]
	private static IEnumerable<CodeInstruction> DestructibleCreateFragmentsTranspiler(
		IEnumerable<CodeInstruction> instructions)
	{
		foreach (var instruction in instructions)
		{
			if (instruction.Calls(RendererIsVisibleGetter))
			{
				yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(
					typeof(IndirectRendererPatches),
					nameof(IsVisibleForFragments)));
				continue;
			}

			yield return instruction;
		}
	}

	private static bool IsVisibleForFragments(Renderer renderer)
	{
		return renderer.isVisible ||
			renderer.TryGetComponent<IndirectInstance>(out var instance) && instance.isActiveAndEnabled;
	}
}
