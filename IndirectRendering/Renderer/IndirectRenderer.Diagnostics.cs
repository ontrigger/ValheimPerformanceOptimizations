using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;
using IndirectRendering.Extensions;
using Debug = UnityEngine.Debug;

namespace IndirectRendering;

public sealed partial class IndirectRenderer
{
	private const int ShadowDiagnosticsFrameInterval = 300;
	private const int CullDiagnosticsCameraOverflowIndex = 0;
	private const int CullDiagnosticsShadowOverflowIndex = 1;
	private const int CullDiagnosticsShadowVolumePassOffset = 2;
	private const int CullDiagnosticsShadowReceiverCullOffset
		= CullDiagnosticsShadowVolumePassOffset + MaxDirectionalCascades;
	private const int CullDiagnosticsViewOverflowIndex
		= CullDiagnosticsShadowReceiverCullOffset + MaxDirectionalCascades;
	private const int CullDiagnosticsCount = CullDiagnosticsViewOverflowIndex + 1;
#if DEBUG
	private const int CullDiagnosticsEnabled = 1;
#else
	private const int CullDiagnosticsEnabled = 0;
#endif

#if DEBUG
	private readonly Vector4[] shadowCascadeSpheresReadback = new Vector4[ShadowCascadeSphereCaptureCount];

	private int nextShadowDiagnosticsFrame;

	private int shadowPreparationRerecordCount;
	private int unityShadowSubmissionCount;
	private int shadowCascadePreparationDispatchCount;
#endif
	private int nextTopologyValidationFrame = 300;
	// The cull kernels write to it, so it is bound in Release too, where
	// _CullDiagnosticsEnabled is 0 and nothing reads it back.
	private GraphicsBuffer cullDiagnosticsBuffer;
	#if DEBUG
	private readonly uint[] cullDiagnosticsReset = new uint[CullDiagnosticsCount];
	private readonly uint[] cullDiagnosticsReadback = new uint[CullDiagnosticsCount];
	private int nextCullDiagnosticsValidationFrame = 300;
	#endif

	private void EnsureCullDiagnosticsBuffer()
	{
		if (cullDiagnosticsBuffer != null) { return; }

		cullDiagnosticsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, CullDiagnosticsCount, sizeof(uint));
		// New buffers are uninitialized, and the Debug validation can read it
		// before any frame with instances has reset it.
		cullDiagnosticsBuffer.SetData(new uint[CullDiagnosticsCount]);
		InvalidateShadowRecordings();
	}

	[Conditional("DEBUG")]
	private void LogShadowDiagnostics()
	{
	#if DEBUG
		if (Time.frameCount < nextShadowDiagnosticsFrame)
		{
			return;
		}

		nextShadowDiagnosticsFrame = Time.frameCount + ShadowDiagnosticsFrameInterval;
		var eligibleBatchCount = 0;
		var activeBatchCount = 0;
		foreach (var runtimeBatch in runtimeBatches)
		{
			if (runtimeBatch.ShadowEligible)
			{
				eligibleBatchCount += 1;
				activeBatchCount += runtimeBatch.RegisteredInstanceCount > 0 ? 1 : 0;
			}
		}

		var shadowVolumePasses =
			$"{cullDiagnosticsReadback[CullDiagnosticsShadowVolumePassOffset]}," +
			$"{cullDiagnosticsReadback[CullDiagnosticsShadowVolumePassOffset + 1]}," +
			$"{cullDiagnosticsReadback[CullDiagnosticsShadowVolumePassOffset + 2]}," +
			$"{cullDiagnosticsReadback[CullDiagnosticsShadowVolumePassOffset + 3]}";
		var shadowReceiverCulls =
			$"{cullDiagnosticsReadback[CullDiagnosticsShadowReceiverCullOffset]}," +
			$"{cullDiagnosticsReadback[CullDiagnosticsShadowReceiverCullOffset + 1]}," +
			$"{cullDiagnosticsReadback[CullDiagnosticsShadowReceiverCullOffset + 2]}," +
			$"{cullDiagnosticsReadback[CullDiagnosticsShadowReceiverCullOffset + 3]}";
		var capturedSpheres = "none";
		if (shadowCascadeSpheresReadback[MaxDirectionalCascades].x == 1f)
		{
			capturedSpheres = string.Empty;
			for (var index = 0; index < shadowCascadeCount; index += 1)
			{
				var sphere = shadowCascadeSpheresReadback[index];
				capturedSpheres += $"{(index == 0 ? "" : "; ")}" +
					$"({sphere.x:F1},{sphere.y:F1},{sphere.z:F1}) r={Mathf.Sqrt(sphere.w):F2}";
			}
		}

		Debug.Log(
			$"IndirectRenderer shadow diagnostics: frame={Time.frameCount}, " +
			$"cascades={shadowCascadeCount}, eligibleBatches={eligibleBatchCount}, " +
			$"activeBatches={activeBatchCount}, " +
			$"attached={attachedShadowCascadeCount != 0}, " +
			$"cameraSubmissions={cameraSubmissionCount}, managedShadowSubmissions={unityShadowSubmissionCount}, " +
			$"batchCells={batchCells.CellCount}, " +
			$"cascadePreparationDispatches={shadowCascadePreparationDispatchCount}, " +
			$"instanceSlots={instanceSlots.HighWaterMark}, liveInstances={instanceSlots.LiveCount}, " +
			$"preparationRerecords={shadowPreparationRerecordCount}, " +
			$"volumePasses={shadowVolumePasses}, receiverCulls={shadowReceiverCulls}, " +
			$"capturedSpheres={capturedSpheres}.");
		shadowPreparationRerecordCount = 0;
	#endif
	}

	[Conditional("DEBUG")]
	private void PrepareCullDiagnostics(int instanceCount)
	{
	#if DEBUG
		if (Time.frameCount >= nextCullDiagnosticsValidationFrame)
		{
			ValidateCullDiagnostics();
			nextCullDiagnosticsValidationFrame = Time.frameCount + ShadowDiagnosticsFrameInterval;
		}

		if (instanceCount == 0)
		{
			return;
		}

		cullDiagnosticsBuffer.SetData(cullDiagnosticsReset);
	#endif
	}

	[Conditional("DEBUG")]
	private void ValidateCullDiagnostics()
	{
	#if DEBUG
		if (cullDiagnosticsBuffer == null)
		{
			return;
		}

		cullDiagnosticsBuffer.GetData(cullDiagnosticsReadback);
		shadowCascadeSpheresBuffer?.GetData(shadowCascadeSpheresReadback);
		Debug.Assert(
			cullDiagnosticsReadback[CullDiagnosticsCameraOverflowIndex] == 0,
			"Camera culling visible-range overflow detected.");
		Debug.Assert(
			cullDiagnosticsReadback[CullDiagnosticsShadowOverflowIndex] == 0,
			"Shadow culling visible-range overflow detected.");
		Debug.Assert(
			cullDiagnosticsReadback[CullDiagnosticsViewOverflowIndex] == 0,
			"View culling visible-range overflow detected.");
	#endif
	}

	[Conditional("DEBUG")]
	private static void ValidateShadowBufferStrides()
	{
		Debug.Assert(Marshal.SizeOf<BatchData>() == 32, "Batch data stride must be 32 bytes.");
		Debug.Assert(Marshal.SizeOf<ShadowCullParameters>() == 32, "Shadow parameter stride must be 32 bytes.");
		Debug.Assert(Marshal.SizeOf<ShadowDepthTile>() == 8, "Shadow depth tile stride must be 8 bytes.");
		Debug.Assert(Marshal.SizeOf<ViewData>() == 144, "View data stride must be 144 bytes.");
		Debug.Assert(
			Marshal.SizeOf<ShadowReceiverParameters>() == 144,
			"Shadow receiver parameter stride must be 144 bytes.");
		Debug.Assert(IndirectArgumentWordCount == 5, "Indirect indexed draw arguments must contain five uints.");
	}

	[Conditional("DEBUG")]
	private void ValidateBatchesPeriodically()
	{
		if (Time.frameCount < nextTopologyValidationFrame)
		{
			return;
		}

		nextTopologyValidationFrame = Time.frameCount + 300;
		Debug.Assert(
			runtimeBatches.Count <= BatchDefinitions.Count,
			"Runtime batches outnumber the batch definitions.");
		var registeredInstanceCount = 0;
		for (var batchIndex = 0; batchIndex < runtimeBatches.Count; batchIndex += 1)
		{
			var runtimeBatch = runtimeBatches[batchIndex];
			Debug.Assert(runtimeBatch.Index == batchIndex, "Runtime batch index is stale.");
			registeredInstanceCount += runtimeBatch.RegisteredInstanceCount;
		}

		Debug.Assert(
			instanceSlots.LiveCount == registeredInstanceCount,
			"Live instance slots do not match the registered batch instances.");
	}

	[Conditional("DEBUG")]
	private static void ValidateShadowSubmission(Bounds bounds)
	{
		Debug.Assert(
			bounds.center.IsFinite() && bounds.extents.IsFinite() && bounds.extents.x >= 0f &&
			bounds.extents.y >= 0f && bounds.extents.z >= 0f,
			"Unity-managed shadow batch has invalid world bounds.");
	}
}
