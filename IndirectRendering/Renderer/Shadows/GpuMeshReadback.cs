using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace IndirectRendering;


internal static class GpuMeshReadback
{
	internal static byte[] ReadVertexStream(Mesh mesh, int stream)
	{
		try
		{
			using var buffer = mesh.GetVertexBuffer(stream);
			if (buffer == null) { return null; }

			var data = new byte[buffer.count * buffer.stride];
			buffer.GetData(data);
			return data;
		}
		catch (Exception)
		{
			return null;
		}
	}

	internal static byte[] ReadIndexBuffer(Mesh mesh)
	{
		try
		{
			using var buffer = mesh.GetIndexBuffer();
			if (buffer == null) { return null; }

			var data = new byte[buffer.count * buffer.stride];
			buffer.GetData(data);
			return data;
		}
		catch (Exception)
		{
			return null;
		}
	}

	internal static void ReadIndices(Mesh mesh, int subMeshIndex, byte[] data, List<int> indices)
	{
		var subMesh = mesh.GetSubMesh(subMeshIndex);
		var sixteenBit = mesh.indexFormat == IndexFormat.UInt16;
		var end = subMesh.indexStart + subMesh.indexCount;
		for (var index = subMesh.indexStart; index < end; index += 1)
		{
			var value = sixteenBit
				? BitConverter.ToUInt16(data, index * 2)
				: (int)BitConverter.ToUInt32(data, index * 4);
			indices.Add(value + subMesh.baseVertex);
		}
	}

	internal readonly struct AttributeLayout
	{
		internal readonly VertexAttributeFormat Format;
		internal readonly int Stride;
		internal readonly int Offset;
		internal readonly int ComponentSize;

		internal AttributeLayout(Mesh mesh, VertexAttribute attribute)
		{
			Format = mesh.GetVertexAttributeFormat(attribute);
			Stride = mesh.GetVertexBufferStride(mesh.GetVertexAttributeStream(attribute));
			Offset = mesh.GetVertexAttributeOffset(attribute);
			ComponentSize = GetFormatSize(Format);
		}
	}

	internal static Vector3 ReadAttribute(in AttributeLayout layout, byte[] data, int vertex)
	{
		var offset = vertex * layout.Stride + layout.Offset;
		return new Vector3(
			ReadComponent(layout.Format, data, offset),
			ReadComponent(layout.Format, data, offset + layout.ComponentSize),
			ReadComponent(layout.Format, data, offset + layout.ComponentSize * 2));
	}

	private static int GetFormatSize(VertexAttributeFormat format)
	{
		return format switch
		{
			VertexAttributeFormat.Float16 or VertexAttributeFormat.UNorm16 or VertexAttributeFormat.SNorm16 => 2,
			VertexAttributeFormat.UNorm8 or VertexAttributeFormat.SNorm8 => 1,
			_ => 4,
		};
	}

	private static float ReadComponent(VertexAttributeFormat format, byte[] data, int offset)
	{
		return format switch
		{
			VertexAttributeFormat.Float16 => Mathf.HalfToFloat(BitConverter.ToUInt16(data, offset)),
			VertexAttributeFormat.UNorm16 => BitConverter.ToUInt16(data, offset) / 65535f,
			VertexAttributeFormat.SNorm16 => Mathf.Max(BitConverter.ToInt16(data, offset) / 32767f, -1f),
			VertexAttributeFormat.UNorm8 => data[offset] / 255f,
			VertexAttributeFormat.SNorm8 => Mathf.Max((sbyte)data[offset] / 127f, -1f),
			_ => BitConverter.ToSingle(data, offset),
		};
	}
}
