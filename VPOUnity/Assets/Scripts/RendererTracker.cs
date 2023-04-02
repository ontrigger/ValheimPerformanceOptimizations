using System.Collections.Generic;
using UnityEngine;

public class RendererTracker : MonoBehaviour, IHasID
{
	public int ID { get; set; }
	public MeshRenderer Renderer { get; private set; }

	private void Awake()
	{
		Renderer = GetComponent<MeshRenderer>();
	}

	private void OnEnable()
	{
		ID = OcclusionRenderer.Instance.AddInstance(this);
	}

	private void OnDisable()
	{
		OcclusionRenderer.Instance.RemoveInstance(this);
		ID = -1;
	}
}

public struct RendererTrackerID
{
	public int ID { get; }

	private RendererTrackerID(int id)
	{
		ID = id;
	}
	
	public static Stack<RendererTrackerID> FreeIDs = new Stack<RendererTrackerID>();
}
