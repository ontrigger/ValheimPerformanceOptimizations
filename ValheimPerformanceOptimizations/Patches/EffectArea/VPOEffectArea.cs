using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;

namespace ValheimPerformanceOptimizations.Patches
{
    public class VPOEffectArea : EffectArea
    {
        private readonly List<Character> inside = new List<Character>();

        private new void Awake()
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
            if (ZNet.instance == null)
            {
                return;
            }
            
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

        private void OnTriggerEnter(Collider other)
        {
            if (ZNet.instance == null)
            {
                return;
            }

            var character = other.GetComponent<Character>();
            if (character && character.IsOwner())
            {
                inside.Add(character);
            }
        }

        private void OnTriggerExit(Collider other)
        {
            if (ZNet.instance == null)
            {
                return;
            }

            var character = other.GetComponent<Character>();
            if (character && character.IsOwner())
            {
                inside.Remove(character);
            }
        }

        private new void OnTriggerStay(Collider collider) { }
    }
}