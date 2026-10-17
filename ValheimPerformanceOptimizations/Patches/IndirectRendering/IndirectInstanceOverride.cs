using System.Runtime.InteropServices;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches.IndirectRendering;

/// <summary>
/// A piece's MaterialMan values, read by the piece shaders as
/// _VPOInstanceOverrides; see VPOBatchedPrefab.ApplyMaterialProperties.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct VPOIndirectInstanceOverride
{
	internal const uint HasColor = 1;
	internal const uint HasEmissionColor = 2;
	internal const uint HasUVOffset = 4;
	internal const uint HasTakingAshlandsDamage = 8;

	internal Vector4 Color;
	internal Vector4 EmissionColor;
	internal Vector4 UVOffset;
	internal float TakingAshlandsDamage;
	internal uint Flags;
	private uint _padding0;
	private uint _padding1;

	internal readonly bool Equals(in VPOIndirectInstanceOverride other)
	{
		return Flags == other.Flags &&
			Color == other.Color &&
			EmissionColor == other.EmissionColor &&
			UVOffset == other.UVOffset &&
			TakingAshlandsDamage.Equals(other.TakingAshlandsDamage);
	}
}
