using System.Collections.Generic;
using UnityEngine;

namespace IndirectRendering;

/// <summary>
/// Copies of batch materials on a replacement shader, with the source's
/// matching properties and keywords; see IndirectRenderer.RenderViewWithShader.
/// </summary>
internal sealed class ReplacementMaterialCache
{
	private readonly Dictionary<(Material, Shader), Material> replacementBySourceAndShader = new();

	internal Material Get(Material source, Shader shader)
	{
		if (replacementBySourceAndShader.TryGetValue((source, shader), out var replacement) && replacement)
		{
			return replacement;
		}

		replacement = new Material(shader)
		{
			name = $"{source.name} ({shader.name})", hideFlags = HideFlags.HideAndDontSave, enableInstancing = true,
		};
		replacement.CopyMatchingPropertiesFromMaterial(source);
		foreach (var keyword in source.shaderKeywords)
		{
			replacement.EnableKeyword(keyword);
		}

		replacementBySourceAndShader[(source, shader)] = replacement;
		return replacement;
	}

	internal void Clear()
	{
		foreach (var replacement in replacementBySourceAndShader.Values)
		{
			if (replacement)
			{
				Object.Destroy(replacement);
			}
		}

		replacementBySourceAndShader.Clear();
	}
}
