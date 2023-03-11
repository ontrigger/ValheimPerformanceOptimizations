using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches
{
	[HarmonyPatch]
	public class VPOEffectArea : EffectArea
	{
		private readonly List<Character> inside = new();

		new private void Awake()
		{
			if (m_characterMask == 0)
			{
				m_characterMask = LayerMask.GetMask("character_trigger");
			}

			m_collider = GetComponent<Collider>();
			m_allAreas.Add(this);
		}

		private void Update()
		{
			if (ZNet.instance == null) { return; }

			inside.RemoveAll(character => character == null);
			foreach (var character in inside)
			{
				if (!character.IsOwner())
				{
					continue;
				}

				if (!string.IsNullOrEmpty(m_statusEffect))
				{
					character.GetSEMan().AddStatusEffect(m_statusEffect, true);
				}

				if ((m_type & Type.Heat) != 0)
				{
					character.OnNearFire(transform.position);
				}
			}
		}

		new private void OnDestroy()
		{
			m_allAreas.Remove(this);
		}

		private void OnTriggerEnter(Collider other)
		{
			if (ZNet.instance == null) { return; }

			var character = other.GetComponent<Character>();
			if (character && character.IsOwner())
			{
				inside.Add(character);
			}
		}

		private void OnTriggerExit(Collider other)
		{
			if (ZNet.instance == null) { return; }

			var character = other.GetComponent<Character>();
			if (character && character.IsOwner())
			{
				inside.Remove(character);
			}
		}

		new private void OnTriggerStay(Collider collider) { }

		[HarmonyPatch(typeof(EffectArea), nameof(EffectArea.Awake))]
		private static bool Prefix(EffectArea __instance)
		{
			var newEffectArea = __instance.gameObject.AddComponent<VPOEffectArea>();

			newEffectArea.m_type = __instance.m_type;
			newEffectArea.m_statusEffect = __instance.m_statusEffect;

			Destroy(__instance);

			return false;
		}
	}
}
