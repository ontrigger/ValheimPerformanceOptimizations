using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.UI;
namespace ValheimPerformanceOptimizations.Patches.UIOptimization
{
	/// <summary>
	/// UIInputHint rebuilds layout every frame.
	/// This patch only rebuilds layout when the input method changes
	/// </summary>
	[HarmonyPatch]
	// ReSharper disable once InconsistentNaming
	public class VPOUIInputHint : UIInputHint
	{
		private InputMethod lastInputMethod = InputMethod.Invalid;

		private enum InputMethod
		{
			Invalid = -1,
			Keyboard,
			Gamepad
		}
		
		[HarmonyPatch(typeof(UIInputHint), nameof(UIInputHint.Start))]
		private static bool Prefix(UIInputHint __instance)
		{
			var inputHint = __instance.gameObject.AddComponent<VPOUIInputHint>();
			inputHint.m_gamepadHint = __instance.m_gamepadHint;
			inputHint.m_mouseKeyboardHint = __instance.m_mouseKeyboardHint;

			Destroy(__instance);

			return false;
		}

		private new void Start()
		{
			m_group = GetComponentInParent<UIGroupHandler>();
			m_button = GetComponent<Button>();
			if (m_gamepadHint)
			{
				m_gamepadHint.gameObject.SetActive(false);
			}
			if (m_mouseKeyboardHint)
			{
				m_mouseKeyboardHint.gameObject.SetActive(false);
			}
		}

		private void OnEnable()
		{
			lastInputMethod = InputMethod.Invalid;
		}

		private new void Update()
		{
			var flag = (m_button == null || m_button.IsInteractable()) && (m_group == null || m_group.IsActive());

			var inputMethod = ZInput.IsGamepadActive() ? InputMethod.Gamepad : InputMethod.Keyboard;
			if (lastInputMethod == inputMethod) { return; }
			
			Profiler.BeginSample("force layout");
			if (m_gamepadHint != null)
			{
				m_gamepadHint.gameObject.SetActive(flag && ZInput.IsGamepadActive());
				LayoutRebuilder.ForceRebuildLayoutImmediate(m_gamepadHint.transform as RectTransform);
			}
			if (m_mouseKeyboardHint != null)
			{
				m_mouseKeyboardHint.gameObject.SetActive(flag && ZInput.IsMouseActive());
				LayoutRebuilder.ForceRebuildLayoutImmediate(m_mouseKeyboardHint.transform as RectTransform);
			}
			Profiler.EndSample();

			lastInputMethod = ZInput.IsGamepadActive() ? InputMethod.Gamepad : InputMethod.Keyboard;
		}
	}
}
