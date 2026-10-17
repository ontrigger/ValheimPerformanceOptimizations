using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

public static class ShaderVariantExporter
{
	[Serializable]
	private sealed class ExportManifest
	{
		public string unityVersion;
		public string shaderAsset;
		public string shaderName;
		public string[] keywords;
		public List<VariantRecord> variants = new();
	}

	[Serializable]
	private sealed class VariantRecord
	{
		public int subshader;
		public int pass;
		public string passName;
		public string stage;
		public bool success;
		public int byteLength;
		public string bytecodeFile;
		public string preprocessedFile;
		public string[] messages;
	}

	public static void ExportFromCommandLine()
	{
		try
		{
			Export();
		}
		catch (Exception exception)
		{
			Debug.LogException(exception);
			EditorApplication.Exit(1);
		}
	}

	private static void Export()
	{
		var arguments = Environment.GetCommandLineArgs();
		var shaderAsset = GetArgument(arguments, "-shaderAsset") ?? "Assets/Shaders/Standard.shader";
		var outputArgument = GetArgument(arguments, "-shaderOutput") ?? "ShaderDecomp/Compiled";
		var keywords = SplitKeywords(GetArgument(arguments, "-shaderKeywords"));
		var subshaderFilter = ParseOptionalInt(GetArgument(arguments, "-shaderSubshader"));
		var passFilter = ParseOptionalInt(GetArgument(arguments, "-shaderPass"));

		var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
		var outputDirectory = Path.IsPathRooted(outputArgument)
			? Path.GetFullPath(outputArgument)
			: Path.GetFullPath(Path.Combine(projectRoot, outputArgument));

		AssetDatabase.ImportAsset(shaderAsset, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
		var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderAsset);
		if (shader == null)
		{
			throw new InvalidOperationException($"Could not load shader asset '{shaderAsset}'.");
		}

		Directory.CreateDirectory(outputDirectory);
		var manifest = new ExportManifest
		{
			unityVersion = Application.unityVersion,
			shaderAsset = shaderAsset,
			shaderName = shader.name,
			keywords = keywords,
		};

		var shaderData = ShaderUtil.GetShaderData(shader);
		for (var subshaderIndex = 0; subshaderIndex < shaderData.SerializedSubshaderCount; subshaderIndex++)
		{
			if (subshaderFilter.HasValue && subshaderFilter.Value != subshaderIndex)
			{
				continue;
			}

			var subshader = shaderData.GetSerializedSubshader(subshaderIndex);
			for (var passIndex = 0; passIndex < subshader.PassCount; passIndex++)
			{
				if (passFilter.HasValue && passFilter.Value != passIndex)
				{
					continue;
				}

				var pass = subshader.GetPass(passIndex);
				ExportStage(manifest, pass, subshaderIndex, passIndex, ShaderType.Vertex, keywords, outputDirectory);
				ExportStage(manifest, pass, subshaderIndex, passIndex, ShaderType.Fragment, keywords, outputDirectory);
			}
		}

		var manifestPath = Path.Combine(outputDirectory, "manifest.json");
		File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest, true));
		Debug.Log($"Shader variants exported to: {outputDirectory}");
	}

	private static void ExportStage(
		ExportManifest manifest,
		ShaderData.Pass pass,
		int subshaderIndex,
		int passIndex,
		ShaderType stage,
		string[] keywords,
		string outputDirectory)
	{
		if (!pass.HasShaderStage(stage))
		{
			return;
		}

		var stem = $"sub{subshaderIndex}_pass{passIndex}_{Sanitize(pass.Name)}_{stage.ToString().ToLowerInvariant()}";
		var bytecodeName = stem + ".dxbc";
		var preprocessedName = stem + ".hlsl";
		var compiled = pass.CompileVariant(
			stage,
			keywords,
			ShaderCompilerPlatform.D3D,
			BuildTarget.StandaloneWindows64,
			true);
		var preprocessed = pass.PreprocessVariant(
			stage,
			keywords,
			ShaderCompilerPlatform.D3D,
			BuildTarget.StandaloneWindows64,
			true);

		if (compiled.ShaderData != null && compiled.ShaderData.Length > 0)
		{
			File.WriteAllBytes(Path.Combine(outputDirectory, bytecodeName), compiled.ShaderData);
		}
		if (!string.IsNullOrEmpty(preprocessed.PreprocessedCode))
		{
			File.WriteAllText(Path.Combine(outputDirectory, preprocessedName), preprocessed.PreprocessedCode);
		}

		manifest.variants.Add(new VariantRecord
		{
			subshader = subshaderIndex,
			pass = passIndex,
			passName = pass.Name,
			stage = stage.ToString(),
			success = compiled.Success,
			byteLength = compiled.ShaderData?.Length ?? 0,
			bytecodeFile = compiled.ShaderData != null && compiled.ShaderData.Length > 0 ? bytecodeName : null,
			preprocessedFile = string.IsNullOrEmpty(preprocessed.PreprocessedCode) ? null : preprocessedName,
			messages = compiled.Messages.Select(message => $"{message.severity}: {message.file}:{message.line}: {message.message}").ToArray(),
		});
	}

	private static string GetArgument(string[] arguments, string name)
	{
		for (var index = 0; index < arguments.Length - 1; index++)
		{
			if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
			{
				return arguments[index + 1];
			}
		}

		return null;
	}

	private static string[] SplitKeywords(string value)
	{
		return string.IsNullOrWhiteSpace(value)
			? Array.Empty<string>()
			: value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
	}

	private static int? ParseOptionalInt(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		if (!int.TryParse(value, out var parsed))
		{
			throw new ArgumentException($"Expected an integer but received '{value}'.");
		}

		return parsed;
	}

	private static string Sanitize(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return "unnamed";
		}

		var invalidCharacters = Path.GetInvalidFileNameChars();
		return new string(value.Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray());
	}
}
