using UnityEngine;

namespace IndirectRendering;

/// <summary>
/// Optional IndirectRenderer settings; anything left unset keeps its default.
/// The renderer copies them when it is created.
/// </summary>
public sealed class IndirectRendererSettings
{
	/// <summary>
	/// Null loads the shaders embedded in this assembly; see
	/// IndirectRendererShaders.
	/// </summary>
	public IndirectRendererShaders Shaders { get; set; }

	/// <summary>
	/// Instances are bucketed into square cells of this size on XZ so whole
	/// batches can be skipped on the CPU.
	/// </summary>
	public float BatchCellSize { get; set; } = 64f;

	/// <summary>
	/// Instances whose projected bounds span less than this fraction of both
	/// screen dimensions are skipped. 0 disables the test. Can be changed later
	/// with IndirectRenderer.SetMinimumScreenFraction.
	/// </summary>
	public float MinimumScreenFraction { get; set; }

	/// <summary>
	/// Picks the shadow casters that shadowed lights draw through native
	/// multi-draw; see MultiDrawCasterResolver. Null draws every caster on its
	/// own.
	/// </summary>
	public MultiDrawCasterResolver MultiDrawCasterResolver { get; set; }
}

/// <summary>
/// Returns the material that draws a batch's shadow caster through native
/// multi-draw, or false to draw the batch on its own. Batches whose caster
/// materials are the same object share one multi-draw per light. The caster
/// shader reads its geometry and instances through VPOMultiDraw.cginc, which
/// also hands it the batch's parameters. The caller owns the caster materials.
/// </summary>
/// <param name="material">The batch's shadow caster material.</param>
public delegate bool MultiDrawCasterResolver(Material material, out Material casterMaterial, out Vector4 batchParameters);
