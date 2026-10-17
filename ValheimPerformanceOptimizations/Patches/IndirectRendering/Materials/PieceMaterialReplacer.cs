using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace ValheimPerformanceOptimizations.Patches.IndirectRendering;

internal static class PieceMaterialReplacer
{
	private const string VanillaPieceShaderName = "Custom/Piece";
	private const string VanillaStaticRockShaderName = "Custom/StaticRock";
	private const string VanillaVegetationShaderName = "Custom/Vegetation";
	private const string VanillaCreatureShaderName = "Custom/Creature";
	private const string BuiltinStandardShaderName = "Standard";
	// VPO/Vegetation has no metal map.
	private const string VegetationMetalMapKeyword = "_USEMETALMAP_ON";
	private const string TriplanarWorldKeyword = "_VPO_TRIPLANAR_WORLD";
	private const string TriplanarLocalKeyword = "_VPO_TRIPLANAR_LOCAL";
	private const string AlphaTestKeyword = "_VPO_ALPHATEST";

	// Kept across ZNetScene reloads: patched prefabs keep these replacements, so
	// location copies registered later must resolve to the same batch definitions.
	private static readonly Dictionary<Material, Material> ReplacementByMaterial = new();

	private static readonly string[] PieceFeatureKeywords =
	{
		"_TWOSIDEDNORMALS_ON", "_VALUENOISEVERTEX_ON", "_ADDRAIN_ON", "_ADDSNOW_ON",
	};

	private static readonly string[] StaticRockFeatureKeywords = { "_FRESNEL_ON", "_ADDSNOW_ON", "_ADDRAIN_ON", };

	private static readonly string[] CreatureFeatureKeywords = { "NOISEGLOW", };

	private static readonly string[] StandardFeatureKeywords =
	{
		"_NORMALMAP", "_METALLICGLOSSMAP", "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A", "_EMISSION",
	};

	private const string StandardAlphaTestKeyword = "_ALPHATEST_ON";

	// Standard features VPO/Standard does not implement.
	private static readonly string[] UnsupportedStandardKeywords =
	{
		"_ALPHABLEND_ON",
		"_ALPHAPREMULTIPLY_ON",
		"_PARALLAXMAP",
		"_DETAIL_MULX2",
		"_SPECULARHIGHLIGHTS_OFF",
		"_GLOSSYREFLECTIONS_OFF",
	};

	/// <summary>
	/// Only the vanilla shaders we have indirect versions of, and those
	/// versions. Mapping any other shader to VPO/Piece drops its vertex
	/// animation: banners and similar wind shaders freeze into a static ripple.
	/// </summary>
	internal static bool IsCompatible(Material material)
	{
		if (!material || !material.shader || !material.HasProperty("_MainTex") || !material.HasProperty("_Color"))
		{
			return false;
		}

		var shaderName = material.shader.name;
		if (shaderName == VanillaVegetationShaderName && material.IsKeywordEnabled(VegetationMetalMapKeyword))
		{
			return false;
		}

		if (shaderName == BuiltinStandardShaderName)
		{
			foreach (var keyword in UnsupportedStandardKeywords)
			{
				if (material.IsKeywordEnabled(keyword))
				{
					return false;
				}
			}
		}

		return GetReplacementShader(shaderName) || IsReplacementShader(material.shader);
	}

	internal static bool TryCreate(Material source, out Material replacement)
	{
		replacement = null;
		if (!IsCompatible(source))
		{
			return false;
		}

		if (IsReplacementShader(source.shader))
		{
			replacement = source;
			return true;
		}

		if (ReplacementByMaterial.TryGetValue(source, out replacement) && replacement)
		{
			return true;
		}

		ReplacementByMaterial.Remove(source);

		var shader = GetReplacementShader(source.shader.name);
		replacement = new Material(source)
		{
			name = source.name + " (IndirectRenderer)", shader = shader, enableInstancing = true,
			renderQueue = source.renderQueue,
		};

		if (shader == PieceShaderBundle.VegetationShader)
		{
			// Leaves are always cut out.
			SetKeyword(replacement, AlphaTestKeyword, true);
		}
		else if (shader == PieceShaderBundle.StandardShader)
		{
			foreach (var keyword in StandardFeatureKeywords)
			{
				SetKeyword(replacement, keyword, source.IsKeywordEnabled(keyword));
			}

			SetKeyword(replacement, AlphaTestKeyword, source.IsKeywordEnabled(StandardAlphaTestKeyword));
		}
		else if (shader == PieceShaderBundle.CreatureShader)
		{
			foreach (var keyword in CreatureFeatureKeywords)
			{
				SetKeyword(replacement, keyword, source.IsKeywordEnabled(keyword));
			}

			SetKeyword(replacement, AlphaTestKeyword, NeedsAlphaTest(replacement));
		}
		else
		{
			var isStaticRock = shader == PieceShaderBundle.StaticRockShader;
			foreach (var keyword in isStaticRock ? StaticRockFeatureKeywords : PieceFeatureKeywords)
			{
				SetKeyword(replacement, keyword, source.IsKeywordEnabled(keyword));
			}

			if (!isStaticRock)
			{
				SetTriplanarKeyword(replacement);
				SetKeyword(replacement, AlphaTestKeyword, NeedsAlphaTest(replacement));
			}
		}

		ReplacementByMaterial.Add(source, replacement);
		return true;
	}

	private static Shader GetReplacementShader(string vanillaShaderName)
	{
		return vanillaShaderName switch
		{
			VanillaPieceShaderName => PieceShaderBundle.Shader,
			VanillaStaticRockShaderName => PieceShaderBundle.StaticRockShader,
			VanillaVegetationShaderName => PieceShaderBundle.VegetationShader,
			VanillaCreatureShaderName => PieceShaderBundle.CreatureShader,
			BuiltinStandardShaderName => PieceShaderBundle.StandardShader,
			_ => null,
		};
	}

	private static bool IsReplacementShader(Shader shader)
	{
		return shader == PieceShaderBundle.Shader ||
			shader == PieceShaderBundle.StaticRockShader ||
			shader == PieceShaderBundle.VegetationShader ||
			shader == PieceShaderBundle.CreatureShader ||
			shader == PieceShaderBundle.StandardShader;
	}

	private static void SetTriplanarKeyword(Material material)
	{
		var triplanar = material.GetFloat("_TriplanarMap") != 0f;
		var local = material.GetFloat("_TriplanarLocalPos") != 0f;
		SetKeyword(material, TriplanarWorldKeyword, triplanar && !local);
		SetKeyword(material, TriplanarLocalKeyword, triplanar && local);
	}

	private static bool NeedsAlphaTest(Material material)
	{
		var cutoff = material.GetFloat("_Cutoff");
		if (cutoff <= 0f)
		{
			return false;
		}

		var albedo = material.mainTexture;
		if (albedo && (albedo is not Texture2D texture || HasAlpha(texture.format)))
		{
			return true;
		}

		var alpha = material.GetColor("_Color").a;
		if (material.GetFloat("_Metallic") != 0f)
		{
			alpha *= Mathf.Min(1f, material.GetColor("_MetalColor").a);
		}

		return alpha < cutoff;
	}

	// DXT1 maps to RGBA_DXT1, whose 1-bit alpha may count as an alpha channel,
	// but the importer only picks DXT1 for textures without alpha.
	private static bool HasAlpha(TextureFormat format)
	{
		return format is not (TextureFormat.DXT1 or TextureFormat.DXT1Crunched) &&
			GraphicsFormatUtility.HasAlphaChannel(format);
	}

	private static void SetKeyword(Material material, string keyword, bool enabled)
	{
		material.SetKeyword(new LocalKeyword(material.shader, keyword), enabled);
	}
}
