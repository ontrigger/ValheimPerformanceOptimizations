using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace IndirectRendering.Burst
{
	/// <summary>
	/// Writes 1 when it runs Burst compiled and 0 when it runs as managed code.
	/// </summary>
	[BurstCompile]
	public struct CheckBurstedJob : IJob
	{
		public NativeArray<int> Flag;

		[BurstDiscard]
		private void MarkNotBursted()
		{
			Flag[0] = 0;
		}

		public void Execute()
		{
			Flag[0] = 1;
			MarkNotBursted();
		}
	}
}
