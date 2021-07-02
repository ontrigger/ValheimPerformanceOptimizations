using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;

namespace ValheimPerformanceOptimizations.Patches
{
    public class VPOEffectArea : EffectArea
    {
        private readonly List<Character> Inside = new List<Character>();

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

            // destroyed characters will cause a memleak
            Profiler.BeginSample("Effect Area Update");
            var j = 0;
            for (var i = 0; i < Inside.Count; i++)
            {
                var character = Inside[i];
                if (!character || !character.IsOwner())
                {
                    j += 1;
                    continue;
                }

                if (!string.IsNullOrEmpty(m_statusEffect))
                {
                    Profiler.BeginSample("status effect add");
                    character.GetSEMan().AddStatusEffect(m_statusEffect, true);
                    Profiler.EndSample();
                }

                if ((m_type & Type.Heat) != 0)
                {
                    character.OnNearFire(transform.position);
                }
            }

            Profiler.EndSample();
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
                Inside.Add(character);
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
                Inside.Remove(character);
            }
        }

        private new void OnTriggerStay(Collider collider) { }
    }
}