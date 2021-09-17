using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;

namespace ValheimPerformanceOptimizations.Patches
{
    [HarmonyPatch]
    public class VPOEffectArea : EffectArea
    {
        private static readonly int EffectAreaTypeCount = Enum.GetNames(typeof(Type)).Length;
        private readonly List<Character> inside = new List<Character>();

        private static readonly BoundsOctree<VPOEffectArea>[] AreaTreeByType
            = new BoundsOctree<VPOEffectArea>[EffectAreaTypeCount];

        private static bool _areaTreeInitialized;

        private static RequiredEffectAreaFields _requiredFieldsHack;

        private new void Awake()
        {
            if (_requiredFieldsHack != null)
            {
                m_type = _requiredFieldsHack.EffectAreaType;
                m_statusEffect = _requiredFieldsHack.StatusEffect;
            }

            if (m_characterMask == 0 || !_areaTreeInitialized)
            {
                m_characterMask = LayerMask.GetMask("character_trigger");

                if (ZNet.instance)
                {
                    for (var i = 0; i < EffectAreaTypeCount; i++)
                    {
                        var refPos = ZNet.instance.GetReferencePosition();
                        AreaTreeByType[i] = new BoundsOctree<VPOEffectArea>(192f, refPos, 8f, 1.1f);
                    }

                    _areaTreeInitialized = true;
                }
            }

            m_collider = GetComponent<Collider>();
            m_allAreas.Add(this);

            if (!_areaTreeInitialized) return;

            var index = GetIndexFromType(m_type);
            AreaTreeByType[index].Add(this, this.m_collider.bounds);
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

        private new void OnDestroy()
        {
            if (!_areaTreeInitialized) { return; }

            var index = GetIndexFromType(m_type);

            var removed = AreaTreeByType[index].Remove(this, m_collider.bounds);
            if (!removed)
            {
                AreaTreeByType[index].Remove(this);
            }
        }

        [HarmonyPatch(typeof(Game), nameof(Game.Shutdown)), HarmonyPostfix]
        private static void Game_Shutdown_Postfix(Game __instance)
        {
            m_characterMask = 0;
            _areaTreeInitialized = false;
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

        private static int GetIndexFromType(Type type)
        {
            var typeValue = (int)type;
            if (typeValue > 512) return EffectAreaTypeCount - 1;
            
            // WHY is Heat 3 and not 1 ????
            if (typeValue == 3) typeValue = 1;

            return (int)Math.Log(typeValue, 2);
        }

        [HarmonyPatch(typeof(EffectArea), nameof(EffectArea.IsPointInsideArea)), HarmonyPrefix]
        private static bool IsPointInsideArea(Vector3 p, Type type, out EffectArea __result, float radius = 0f)
        {
            if (!_areaTreeInitialized)
            {
                __result = null;
                return false;
            }

            var index = GetIndexFromType(type);
            
            Profiler.BeginSample("octree search");
            var collidingWith = new List<VPOEffectArea>();
            AreaTreeByType[index].GetOverlapping(collidingWith, p, radius);

            __result = collidingWith.FirstOrDefault();
            Profiler.EndSample();

            return false;
        }

        [HarmonyPatch(typeof(EffectArea), nameof(EffectArea.Awake))]
        private static bool Prefix(EffectArea __instance)
        {
            _requiredFieldsHack = new RequiredEffectAreaFields(__instance.m_statusEffect, __instance.m_type);
            __instance.gameObject.AddComponent<VPOEffectArea>();
            _requiredFieldsHack = null;

            Destroy(__instance);

            return false;
        }
    }
    
    internal class RequiredEffectAreaFields
    {
        public readonly string StatusEffect;
        public readonly EffectArea.Type EffectAreaType;
        
        public RequiredEffectAreaFields(string statusEffect, EffectArea.Type effectAreaType)
        {
            StatusEffect = statusEffect;
            EffectAreaType = effectAreaType;
        }
    }
}