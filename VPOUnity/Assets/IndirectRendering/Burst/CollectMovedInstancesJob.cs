using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace IndirectRendering.Burst
{
	/// <summary>
	/// What an instance slot keeps on the CPU, so moves can rebuild its whole
	/// instance from its new transform. ComponentId is 0 for free slots.
	/// </summary>
	[StructLayout(LayoutKind.Sequential)]
	public struct InstanceSlot
	{
		public int ComponentId;
		public int BatchIndex;
		public int OverrideIndex;
		// The LOD distances, packed as in objectToWorld's m33.
		public float LodDistances;
		// The LOD reference point in object space, which moves with the instance.
		public float3 LocalLodReference;
		// The world position the instance was last added to the cell grid at.
		public float3 Position;
	}

	[StructLayout(LayoutKind.Sequential)]
	public struct MovedInstance
	{
		public int Slot;
		public int BatchIndex;
		public float3 PreviousPosition;
		public float3 BoundsCenter;
		public float3 BoundsExtents;
		// Complete instance matrices, with their packed bottom rows; see
		// IndirectRenderer.Register.
		public float4x4 ObjectToWorld;
		public float4x4 WorldToObject;
	}

	/// <summary>
	/// Looks up each moved component's slots in ComponentSlots and writes
	/// a MovedInstance per slot: its new instance, built from the transform and
	/// the slot's InstanceSlot, and its batch's mesh bounds in world space. Each
	/// slot's Position is updated. Components not in the map are skipped.
	/// When Moved is too small, MovedCount[0] still counts every slot, but only
	/// Moved.Length are written and no Position is updated.
	/// </summary>
	[BurstCompile]
	public struct CollectMovedInstancesJob : IJob
	{
		[ReadOnly] public NativeArray<int> ComponentIds;
		[ReadOnly] public NativeArray<float4x4> LocalToWorld;
		// Component instance id to its instance slots.
		[ReadOnly] public NativeParallelMultiHashMap<int, int> ComponentSlots;
		// Indexed by slot.
		public NativeArray<InstanceSlot> Slots;
		// Indexed by batch.
		[ReadOnly] public NativeArray<float3> BatchBoundsCenters;
		[ReadOnly] public NativeArray<float3> BatchBoundsExtents;

		[WriteOnly] public NativeArray<MovedInstance> Moved;
		public NativeArray<int> MovedCount;

		public void Execute()
		{
			var count = 0;
			for (var i = 0; i < ComponentIds.Length; i += 1)
			{
				if (ComponentSlots.TryGetFirstValue(ComponentIds[i], out _, out var iterator))
				{
					count += 1;
					while (ComponentSlots.TryGetNextValue(out _, ref iterator))
					{
						count += 1;
					}
				}
			}

			MovedCount[0] = count;
			if (count > Moved.Length)
			{
				return;
			}

			count = 0;
			for (var i = 0; i < ComponentIds.Length; i += 1)
			{
				if (!ComponentSlots.TryGetFirstValue(ComponentIds[i], out var slot, out var iterator))
				{
					continue;
				}

				// A component's slots share its transform, so the inverse is taken once.
				var transform = new ComponentTransform(LocalToWorld[i]);
				do
				{
					Moved[count] = Build(slot, transform);
					count += 1;
				}
				while (ComponentSlots.TryGetNextValue(out slot, ref iterator));
			}
		}

		private struct ComponentTransform
		{
			public float4x4 LocalToWorld;
			public float4x4 WorldToObject;
			public float3 AbsoluteX;
			public float3 AbsoluteY;
			public float3 AbsoluteZ;

			public ComponentTransform(float4x4 localToWorld)
			{
				var rotationScale = new float3x3(localToWorld.c0.xyz, localToWorld.c1.xyz, localToWorld.c2.xyz);
				var inverse = math.inverse(rotationScale);
				LocalToWorld = localToWorld;
				WorldToObject = new float4x4(
					new float4(inverse.c0, 0f),
					new float4(inverse.c1, 0f),
					new float4(inverse.c2, 0f),
					new float4(-math.mul(inverse, localToWorld.c3.xyz), 1f));
				AbsoluteX = math.abs(rotationScale.c0);
				AbsoluteY = math.abs(rotationScale.c1);
				AbsoluteZ = math.abs(rotationScale.c2);
			}
		}

		private MovedInstance Build(int slotIndex, in ComponentTransform transform)
		{
			var slot = Slots[slotIndex];
			var localToWorld = transform.LocalToWorld;
			// The bottom rows carry the override index, batch index plus one, LOD
			// reference point and distances.
			var worldToObject = transform.WorldToObject;
			worldToObject.c0.w = slot.OverrideIndex;
			worldToObject.c1.w = slot.BatchIndex + 1;

			var lodReference = math.transform(localToWorld, slot.LocalLodReference);
			var objectToWorld = localToWorld;
			objectToWorld.c0.w = lodReference.x;
			objectToWorld.c1.w = lodReference.y;
			objectToWorld.c2.w = lodReference.z;
			objectToWorld.c3.w = slot.LodDistances;

			var previousPosition = slot.Position;
			slot.Position = localToWorld.c3.xyz;
			Slots[slotIndex] = slot;

			// Same as BoundsX.TransformBy.
			var extents = BatchBoundsExtents[slot.BatchIndex];
			return new MovedInstance
			{
				Slot = slotIndex,
				BatchIndex = slot.BatchIndex,
				PreviousPosition = previousPosition,
				BoundsCenter = math.transform(localToWorld, BatchBoundsCenters[slot.BatchIndex]),
				BoundsExtents = transform.AbsoluteX * extents.x
					+ transform.AbsoluteY * extents.y
					+ transform.AbsoluteZ * extents.z,
				ObjectToWorld = objectToWorld,
				WorldToObject = worldToObject,
			};
		}
	}
}
