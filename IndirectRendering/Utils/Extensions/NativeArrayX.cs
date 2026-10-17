using Unity.Collections;
using UnityEngine;

namespace IndirectRendering.Extensions
{
	public static class NativeArrayX
	{
		public static void EnsureCapacity<T>(ref this NativeArray<T> array, int length, int minimum = 16)
			where T : struct
		{
			if (array.IsCreated && array.Length >= length)
			{
				return;
			}

			array.DisposeIfCreated();
			array = new NativeArray<T>(
				Mathf.NextPowerOfTwo(Mathf.Max(length, minimum)),
				Allocator.Persistent,
				NativeArrayOptions.UninitializedMemory);
		}

		// New elements are zeroed.
		public static void GrowCapacity<T>(ref this NativeArray<T> array, int length, int minimum = 16)
			where T : struct
		{
			if (array.IsCreated && array.Length >= length)
			{
				return;
			}

			var grown = new NativeArray<T>(Mathf.NextPowerOfTwo(Mathf.Max(length, minimum)), Allocator.Persistent);
			if (array.IsCreated)
			{
				NativeArray<T>.Copy(array, grown, array.Length);
				array.Dispose();
			}

			array = grown;
		}

		public static void DisposeIfCreated<T>(ref this NativeArray<T> array) where T : struct
		{
			if (array.IsCreated)
			{
				array.Dispose();
			}

			array = default;
		}
	}
}
