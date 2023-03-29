﻿using UnityEngine;
[RequireComponent(typeof(Camera))]
public class SimpleFlyCamera : MonoBehaviour
{
	public float acceleration = 50;       // how fast you accelerate
	public float accSprintMultiplier = 4; // how much faster you go when "sprinting"
	public float lookSensitivity = 1;     // mouse look sensitivity
	public float dampingCoefficient = 5;  // how quickly you break to a halt after you stop your input
	public bool focusOnEnable = true;     // whether or not to focus and lock cursor immediately on enable

	private Vector3 velocity; // current velocity

	private static bool Focused
	{
		get
		{
			return Cursor.lockState == CursorLockMode.Locked;
		}
		set
		{
			Cursor.lockState = value ? CursorLockMode.Locked : CursorLockMode.None;
			Cursor.visible = value == false;
		}
	}

	private void OnEnable()
	{
		if (focusOnEnable) Focused = true;
	}

	private void OnDisable()
	{
		Focused = false;
	}

	private void Update()
	{
		// Input
		if (Focused)
			UpdateInput();
		else if (Input.GetMouseButtonDown(0))
			Focused = true;

		// Physics
		velocity = Vector3.Lerp(velocity, Vector3.zero, dampingCoefficient * Time.deltaTime);
		transform.position += velocity * Time.deltaTime;
	}

	private void UpdateInput()
	{
		// Position
		velocity += GetAccelerationVector() * Time.deltaTime;

		// Rotation
		var mouseDelta = lookSensitivity * new Vector2(Input.GetAxis("Mouse X"), -Input.GetAxis("Mouse Y"));
		var rotation = transform.rotation;
		var horiz = Quaternion.AngleAxis(mouseDelta.x, Vector3.up);
		var vert = Quaternion.AngleAxis(mouseDelta.y, Vector3.right);
		transform.rotation = horiz * rotation * vert;

		// Leave cursor lock
		if (Input.GetKeyDown(KeyCode.Escape))
			Focused = false;
	}

	private Vector3 GetAccelerationVector()
	{
		Vector3 moveInput = default;

		void AddMovement(KeyCode key, Vector3 dir)
		{
			if (Input.GetKey(key))
				moveInput += dir;
		}

		AddMovement(KeyCode.W, Vector3.forward);
		AddMovement(KeyCode.S, Vector3.back);
		AddMovement(KeyCode.D, Vector3.right);
		AddMovement(KeyCode.A, Vector3.left);
		AddMovement(KeyCode.Space, transform.InverseTransformDirection(Vector3.up));
		AddMovement(KeyCode.LeftControl, transform.InverseTransformDirection(Vector3.down));
		var direction = transform.TransformVector(moveInput.normalized);

		if (Input.GetKey(KeyCode.LeftShift))
			return direction * (acceleration * accSprintMultiplier); // "sprinting"
		return direction * acceleration;                             // "walking"
	}
}