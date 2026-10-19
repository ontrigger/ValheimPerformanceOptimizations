using System.Collections.Generic;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches.IndirectRendering;

/// <summary>
/// The indirect renderer's MultiDrawCasterResolver: opaque VPO/Piece,
/// VPO/Standard and VPO/StaticRock casters draw with Hidden/VPO/MultiDrawShadowCaster,
/// one material per cull mode, with the ripple as batch parameters.
/// </summary>
internal static class MultiDrawCasters
{
	private const string AlphaTestKeyword = "_VPO_ALPHATEST";
	private const float CullBack = 2f;

	private static readonly int CullId = Shader.PropertyToID("_Cull");
	private static readonly int RippleDistanceId = Shader.PropertyToID("_RippleDistance");
	private static readonly int RippleFrequencyId = Shader.PropertyToID("_RippleFreq");

	private static readonly Dictionary<float, Material> CasterMaterialByCull = new();

	internal static bool TryGetCaster(Material material, out Material casterMaterial, out Vector4 batchParameters)
	{
		casterMaterial = null;
		batchParameters = Vector4.zero;
		var shader = material.shader;
		var cull = CullBack;
		if (shader == PieceShaderBundle.Shader)
		{
			cull = material.GetFloat(CullId);
			// VPO/Piece offsets the ripple's phase by position.
			batchParameters = new Vector4(material.GetFloat(RippleDistanceId), material.GetFloat(RippleFrequencyId), 1f, 0f);
		}
		else if (shader == PieceShaderBundle.StaticRockShader)
		{
			batchParameters = new Vector4(material.GetFloat(RippleDistanceId), material.GetFloat(RippleFrequencyId), 0f, 0f);
		}
		else if (shader != PieceShaderBundle.StandardShader)
		{
			return false;
		}

		if (material.IsKeywordEnabled(AlphaTestKeyword) || !PieceShaderBundle.MultiDrawShadowCasterShader)
		{
			return false;
		}

		if (!CasterMaterialByCull.TryGetValue(cull, out casterMaterial) || !casterMaterial)
		{
			casterMaterial = new Material(PieceShaderBundle.MultiDrawShadowCasterShader)
			{
				name = $"VPO Multi-Draw Caster (cull {cull})", hideFlags = HideFlags.HideAndDontSave,
			};
			casterMaterial.SetFloat(CullId, cull);
			CasterMaterialByCull[cull] = casterMaterial;
		}

		return true;
	}

	internal static void Release()
	{
		foreach (var casterMaterial in CasterMaterialByCull.Values)
		{
			if (casterMaterial)
			{
				Object.Destroy(casterMaterial);
			}
		}

		CasterMaterialByCull.Clear();
	}
}
