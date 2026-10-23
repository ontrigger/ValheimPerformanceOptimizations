using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace IndirectRendering;

/// <summary>
/// Shadow caster meshes copied into one shared vertex buffer and index buffer,
/// so casters of different meshes can share a native multi-draw. Each mesh's
/// vertices are copied once, with its indices rebased to address them.
/// </summary>
internal sealed class CasterGeometryBuffer
{
	[StructLayout(LayoutKind.Sequential)]
	private struct CasterVertex
	{
		internal Vector3 Position;
		internal Vector3 Normal;
	}

	private readonly List<CasterVertex> vertices = new();
	private readonly List<int> indices = new();
	private readonly Dictionary<Mesh, int> vertexBaseByMesh = new();
	private readonly Dictionary<(Mesh, int), (int StartIndex, int IndexCount)> indexRangeByMeshAndSubMesh = new();
	private readonly List<Vector3> positionsScratch = new();
	private readonly List<Vector3> normalsScratch = new();
	private readonly List<int> indicesScratch = new();
	// Index buffers read back since the last upload, shared by a mesh's submeshes.
	private readonly Dictionary<Mesh, byte[]> gpuIndexDataByMesh = new();
	private GraphicsBuffer vertexBuffer;
	private GraphicsBuffer indexBuffer;

	internal GraphicsBuffer VertexBuffer => vertexBuffer;
	internal GraphicsBuffer IndexBuffer => indexBuffer;

	/// <summary>
	/// Adds the submesh if it is not in the shared buffers yet. Only triangle
	/// meshes with normals can be added.
	/// </summary>
	/// <returns>Whether the submesh is in the shared buffers.</returns>
	internal bool TryGetIndexRange(Mesh mesh, int subMeshIndex, out (int StartIndex, int IndexCount) indexRange)
	{
		if (indexRangeByMeshAndSubMesh.TryGetValue((mesh, subMeshIndex), out indexRange)) { return true; }

		if (mesh.GetTopology(subMeshIndex) != MeshTopology.Triangles ||
		    !mesh.HasVertexAttribute(VertexAttribute.Normal))
		{
			return false;
		}

		if (!vertexBaseByMesh.TryGetValue(mesh, out var vertexBase))
		{
			if (!TryReadVertices(mesh)) { return false; }

			vertexBase = vertices.Count;
			for (var index = 0; index < positionsScratch.Count; index += 1)
			{
				vertices.Add(new CasterVertex { Position = positionsScratch[index], Normal = normalsScratch[index] });
			}

			vertexBaseByMesh.Add(mesh, vertexBase);
		}

		if (!TryReadIndices(mesh, subMeshIndex)) { return false; }

		indexRange = (indices.Count, indicesScratch.Count);
		foreach (var index in indicesScratch)
		{
			indices.Add(vertexBase + index);
		}

		indexRangeByMeshAndSubMesh.Add((mesh, subMeshIndex), indexRange);
		return true;
	}

	private bool TryReadVertices(Mesh mesh)
	{
		if (mesh.isReadable)
		{
			mesh.GetVertices(positionsScratch);
			mesh.GetNormals(normalsScratch);
			return true;
		}

		positionsScratch.Clear();
		normalsScratch.Clear();
		var positionStream = mesh.GetVertexAttributeStream(VertexAttribute.Position);
		var normalStream = mesh.GetVertexAttributeStream(VertexAttribute.Normal);
		var positionData = GpuMeshReadback.ReadVertexStream(mesh, positionStream);
		var normalData = normalStream == positionStream
			? positionData
			: GpuMeshReadback.ReadVertexStream(mesh, normalStream);
		if (positionData == null || normalData == null) { return false; }

		var positionLayout = new GpuMeshReadback.AttributeLayout(mesh, VertexAttribute.Position);
		var normalLayout = new GpuMeshReadback.AttributeLayout(mesh, VertexAttribute.Normal);
		var vertexCount = mesh.vertexCount;
		for (var vertex = 0; vertex < vertexCount; vertex += 1)
		{
			positionsScratch.Add(GpuMeshReadback.ReadAttribute(positionLayout, positionData, vertex));
			normalsScratch.Add(GpuMeshReadback.ReadAttribute(normalLayout, normalData, vertex));
		}

		return true;
	}

	private bool TryReadIndices(Mesh mesh, int subMeshIndex)
	{
		if (mesh.isReadable)
		{
			mesh.GetIndices(indicesScratch, subMeshIndex, true);
			return true;
		}

		indicesScratch.Clear();
		if (!gpuIndexDataByMesh.TryGetValue(mesh, out var data))
		{
			data = GpuMeshReadback.ReadIndexBuffer(mesh);
			gpuIndexDataByMesh.Add(mesh, data);
		}

		if (data == null) { return false; }

		GpuMeshReadback.ReadIndices(mesh, subMeshIndex, data, indicesScratch);
		return true;
	}

	internal void Upload()
	{
		ReleaseBuffers();
		vertexBuffer = new GraphicsBuffer(
			GraphicsBuffer.Target.Structured,
			vertices.Count,
			Marshal.SizeOf<CasterVertex>());
		vertexBuffer.SetData(vertices);
		indexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, indices.Count, sizeof(int));
		indexBuffer.SetData(indices);
		gpuIndexDataByMesh.Clear();
	}

	internal void Release()
	{
		ReleaseBuffers();
		vertices.Clear();
		indices.Clear();
		vertexBaseByMesh.Clear();
		indexRangeByMeshAndSubMesh.Clear();
		gpuIndexDataByMesh.Clear();
	}

	private void ReleaseBuffers()
	{
		vertexBuffer?.Release();
		vertexBuffer = null;
		indexBuffer?.Release();
		indexBuffer = null;
	}
}
