using IndirectRendering.Collections;
using UnityEngine;

namespace IndirectRendering;

/// <summary>
/// Draws its transform with the batches in batchIds while it is enabled and
/// follows that transform as it moves, even while its own renderer is
/// disabled. It registers with IndirectRenderer.Current.
/// </summary>
public sealed class IndirectInstance : MonoBehaviour
{
	public int[] batchIds;
	// The LOD reference point in lodRoot's space and the camera distances
	// lodRoot's scale multiplies; see LodRange.
	public Transform lodRoot;
	public Vector3 lodReferencePoint;
	public float lodStartDistance;
	public float lodEndDistance = float.PositiveInfinity;

	private GenerationalHandle[] handles;
	private int overrideIndex;
	private bool started;

	/// <summary>
	/// overrideIndex is from IndirectRenderer.AllocateOverride, or 0 for none.
	/// </summary>
	public void SetOverrideIndex(int index)
	{
		overrideIndex = index;
		var renderer = IndirectRenderer.Current;
		if (handles == null || renderer == null) { return; }

		foreach (var handle in handles)
		{
			renderer.SetInstanceOverride(handle, index);
		}
	}

	private void Start()
	{
		started = true;
		Register();
	}

	private void OnEnable()
	{
		if (started)
		{
			Register();
		}
	}

	private void OnDisable()
	{
		var renderer = IndirectRenderer.Current;
		if (handles == null || renderer == null) { return; }

		for (var i = 0; i < handles.Length; i += 1)
		{
			renderer.Unregister(ref handles[i]);
		}
	}

	private void Register()
	{
		var renderer = IndirectRenderer.Current;
		if (renderer == null) { return; }

		// Not in Awake: adding this component to an active GameObject runs
		// Awake before batchIds is assigned.
		handles ??= new GenerationalHandle[batchIds.Length];
		var lodRange = LodRange.Pack(lodRoot, lodReferencePoint, lodStartDistance, lodEndDistance);
		var objectToWorld = transform.localToWorldMatrix;
		var instanceId = GetInstanceID();
		for (var i = 0; i < handles.Length; i += 1)
		{
			handles[i] = renderer.Register(batchIds[i], instanceId, objectToWorld, lodRange, overrideIndex);
		}
	}
}
