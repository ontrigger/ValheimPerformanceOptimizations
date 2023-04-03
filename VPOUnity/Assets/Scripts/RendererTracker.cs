using UnityEngine;

public class RendererTracker : MonoBehaviour
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
