using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace IndirectRendering;

/// <summary>
/// Issues native multi-draws through the GfxPluginMDI plugin
/// (github.com/saivs/com.saivs.plugin.mdi), which must sit in
/// valheim_Data/Plugins/x86_64 for Unity to load it before the graphics
/// device. A multi-draw is an empty prime draw that binds the material,
/// properties and index buffer, followed by a plugin event that runs every
/// draw of an argument range against that state. Shaders read their vertices
/// by SV_VertexID and the draw's instance including its startInstance: on
/// D3D from a TEXCOORD7 input the plugin feeds from an identity buffer, on
/// Vulkan from SV_InstanceID; see VPOMultiDraw.cginc.
///
/// The plugin's own C# API stages parameters in a 256-slot ring, which
/// suits command buffers recorded every frame. The renderer replays its
/// command buffers until something changes, so the parameters live in
/// ParamsBlocks that last as long as the recordings instead.
/// </summary>
internal static class MultiDraw
{
	private const string DllName = "GfxPluginMDI";
	// The plugin's draw events take this many ids from its base; the prepare
	// event is the one after them.
	private const int DrawEventCount = 256;
	// Sizes the identity buffer once, before any multi-draw can be in flight;
	// resizing it replaces the buffer under the render thread.
	private const int IdentityBufferSize = 1 << 20;

	// Must match the plugin's MDIParams.
	[StructLayout(LayoutKind.Sequential)]
	private struct NativeParams
	{
		public IntPtr ArgsBuffer;
		public IntPtr IndexBuffer;
		public uint ArgsOffsetBytes;
		public uint MaxDrawCount;
		// 0 for 16-bit indices, 1 for 32-bit.
		public uint IndexFormat;
		public uint Topology;
		public uint Flags;
		public uint Pad;
		public IntPtr CountBuffer;
		public uint CountOffsetBytes;
		public uint Pad2;
	}

	private static readonly int ParamsSize = Marshal.SizeOf<NativeParams>();

	[DllImport(DllName)] private static extern int MDI_IsSupported();
	[DllImport(DllName)] private static extern int MDI_GetBaseEventID();
	[DllImport(DllName)] private static extern IntPtr MDI_GetRenderEventAndDataFunc();
	[DllImport(DllName)] private static extern int MDI_SetMaxInstanceCount(uint maxCount);
	[DllImport(DllName)] private static extern int MDI_UsesPerInstanceVB();
	[DllImport(DllName)] private static extern void MDI_SetLogCallback(IntPtr callback);

	[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
	private delegate void NativeLogDelegate(IntPtr message);

	private static bool _initialized;
	// Native code holds its function pointer for the rest of the session.
	private static NativeLogDelegate _logDelegate;
	private static int _eventId;
	private static IntPtr _renderEventFunc;
	// D3D: the instance comes from the plugin's identity buffer.
	private static bool _usesIdentityBuffer;
	// The D3D prime draw. Its TEXCOORD7 stream makes Unity create an input
	// layout with that element, which the plugin moves to its identity buffer;
	// the plugin then binds the multi-draw's index buffer itself.
	private static Mesh _primeMesh;
	// The Vulkan prime draw's arguments: no instances. The plugin draws with the
	// index buffer the prime draw binds and skips the draws when it is another.
	private static GraphicsBuffer _primeArguments;

	internal static bool Supported { get; private set; }

	/// <summary>
	/// The most instances a view's visible range may hold: startInstance plus
	/// the instance count must stay inside the identity buffer.
	/// </summary>
	internal static int MaxInstanceCount => _usesIdentityBuffer ? IdentityBufferSize : int.MaxValue;

	[AOT.MonoPInvokeCallback(typeof(NativeLogDelegate))]
	private static void OnNativeLog(IntPtr message)
	{
		if (message != IntPtr.Zero)
		{
			Debug.Log("IndirectRendering: " + Marshal.PtrToStringAnsi(message)?.TrimEnd('\n', '\r'));
		}
	}

	internal static void EnsureInitialized()
	{
		if (_initialized)
		{
			return;
		}

		_initialized = true;
		try
		{
			_logDelegate = OnNativeLog;
			MDI_SetLogCallback(Marshal.GetFunctionPointerForDelegate(_logDelegate));
			// The caster shaders read their instance for these APIs only.
			var knownDevice = SystemInfo.graphicsDeviceType is
				GraphicsDeviceType.Direct3D11 or GraphicsDeviceType.Direct3D12 or GraphicsDeviceType.Vulkan;
			Supported = knownDevice && MDI_IsSupported() != 0 && MDI_SetMaxInstanceCount(IdentityBufferSize) != 0;
			if (Supported)
			{
				// The plugin reads the slot from the event id only to stage the
				// parameters it is handed, then draws right away, so one slot serves
				// every multi-draw.
				_eventId = MDI_GetBaseEventID();
				_renderEventFunc = MDI_GetRenderEventAndDataFunc();
				_usesIdentityBuffer = MDI_UsesPerInstanceVB() != 0;
				if (_usesIdentityBuffer)
				{
					_primeMesh = CreatePrimeMesh();
				}
				else
				{
					_primeArguments = new GraphicsBuffer(
						GraphicsBuffer.Target.IndirectArguments,
						1,
						GraphicsBuffer.IndirectDrawIndexedArgs.size);
					_primeArguments.SetData(new GraphicsBuffer.IndirectDrawIndexedArgs[1]);
				}
			}

			Debug.Log($"IndirectRendering: native multi-draw supported={Supported}, device={SystemInfo.graphicsDeviceType}.");
		}
		catch (Exception exception)
		{
			Supported = false;
			Debug.Log($"IndirectRendering: no native multi-draw: {exception.GetType().Name}: {exception.Message}");
		}
	}

	/// <summary>
	/// Parameters for the multi-draws of a command buffer. The plugin reads them
	/// when the buffer executes, so they must outlive its recording. Growing the
	/// block frees the old memory at once, so whoever grows it re-records every
	/// command buffer that points into it before any of them executes.
	/// </summary>
	internal sealed class ParamsBlock : IDisposable
	{
		private IntPtr memory;
		private int capacity;

		internal void EnsureCapacity(int count)
		{
			if (count <= capacity) { return; }

			Dispose();
			capacity = Mathf.NextPowerOfTwo(count);
			memory = Marshal.AllocHGlobal(capacity * ParamsSize);
		}

		/// <param name="indexBuffer">32-bit triangle list indices.</param>
		internal IntPtr Write(
			int index,
			GraphicsBuffer arguments,
			int firstDraw,
			int drawCount,
			GraphicsBuffer indexBuffer)
		{
			var pointer = memory + index * ParamsSize;
			Marshal.StructureToPtr(
				new NativeParams
				{
					ArgsBuffer = arguments.GetNativeBufferPtr(),
					IndexBuffer = indexBuffer.GetNativeBufferPtr(),
					ArgsOffsetBytes = (uint)(firstDraw * GraphicsBuffer.IndirectDrawIndexedArgs.size),
					MaxDrawCount = (uint)drawCount,
					IndexFormat = 1u,
					Topology = (uint)MeshTopology.Triangles,
				},
				pointer,
				false);
			return pointer;
		}

		/// <summary>
		/// Writes the parameters of RecordPrepare for arguments.
		/// </summary>
		internal IntPtr WritePrepare(int index, GraphicsBuffer arguments)
		{
			var pointer = memory + index * ParamsSize;
			Marshal.StructureToPtr(new NativeParams { ArgsBuffer = arguments.GetNativeBufferPtr() }, pointer, false);
			return pointer;
		}

		public void Dispose()
		{
			if (memory != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(memory);
			}

			memory = IntPtr.Zero;
			capacity = 0;
		}
	}

	/// <summary>
	/// Makes what the GPU wrote to the arguments buffer that
	/// ParamsBlock.WritePrepare wrote to parameters visible to the multi-draws
	/// that read it. Record after the writes and before the multi-draws. Vulkan
	/// needs it, and records it outside any render pass; D3D does nothing.
	/// </summary>
	internal static void RecordPrepare(CommandBuffer commandBuffer, IntPtr parameters)
	{
		commandBuffer.IssuePluginEventAndData(_renderEventFunc, _eventId + DrawEventCount, parameters);
	}

	/// <summary>
	/// Records the multi-draw that ParamsBlock.Write wrote to parameters, with
	/// the index buffer it was written with.
	/// </summary>
	internal static void Record(
		CommandBuffer commandBuffer,
		IntPtr parameters,
		GraphicsBuffer indexBuffer,
		Material material,
		int shaderPass,
		MaterialPropertyBlock properties)
	{
		if (_usesIdentityBuffer)
		{
			commandBuffer.DrawMesh(_primeMesh, Matrix4x4.identity, material, 0, shaderPass, properties);
		}
		else
		{
			commandBuffer.DrawProceduralIndirect(
				indexBuffer,
				Matrix4x4.identity,
				material,
				shaderPass,
				MeshTopology.Triangles,
				_primeArguments,
				0,
				properties);
		}

		commandBuffer.IssuePluginEventAndData(_renderEventFunc, _eventId, parameters);
	}

	private static Mesh CreatePrimeMesh()
	{
		var mesh = new Mesh { name = "VPO Multi-Draw Prime", hideFlags = HideFlags.HideAndDontSave };
		mesh.SetVertexBufferParams(
			3,
			new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0),
			new VertexAttributeDescriptor(VertexAttribute.TexCoord7, VertexAttributeFormat.UInt32, 1, 1));
		mesh.SetVertexBufferData(new Vector3[3], 0, 0, 3, 0);
		mesh.SetVertexBufferData(new uint[3], 0, 0, 3, 1);
		mesh.SetIndices(new[] { 0, 1, 2 }, MeshTopology.Triangles, 0);
		return mesh;
	}
}
