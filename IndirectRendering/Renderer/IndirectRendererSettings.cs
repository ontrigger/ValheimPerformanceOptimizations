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
}
