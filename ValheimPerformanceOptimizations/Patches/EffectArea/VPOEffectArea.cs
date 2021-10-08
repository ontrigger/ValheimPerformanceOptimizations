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

        private static readonly BoundsOctree<VPOEffectArea>[] AreaTreeByType
            = new BoundsOctree<VPOEffectArea>[EffectAreaTypeCount];

        private static bool _areaTreeInitialized;

        private static RequiredEffectAreaFields _requiredFieldsHack;
        private static readonly HashSet<VPOEffectArea> ChangedTransforms = new HashSet<VPOEffectArea>();

        private readonly List<Character> inside = new List<Character>();

        public Vector3 lastPosition;

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

            transform.hasChanged = false;
            lastPosition = transform.position;
        }

        private void OnEnable()
        {
            var index = GetIndexFromType(m_type);
            InsertAreaWithIndex(index, this);
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

            if (transform.hasChanged)
            {
                transform.hasChanged = false;
                ChangedTransforms.Add(this);
            }
        }

        private new void OnDestroy() { }

        private void OnDisable()
        {
            if (!_areaTreeInitialized || m_collider == null) return;

            var index = GetIndexFromType(m_type);

            RemoveAreaWithIndex(index, this);

            ChangedTransforms.Remove(this);
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

        private new void OnTriggerStay(Collider collider) { }

        private static void InsertAreaWithIndex(int index, VPOEffectArea area)
        {
            if (area.m_collider == null) { return; }

            AreaTreeByType[index].Add(area, area.m_collider.bounds);
            area.lastPosition = area.transform.position;
        }

        private static void RemoveAreaWithIndex(int index, VPOEffectArea area)
        {
            if (area.m_collider == null) { return; }
            
            var bounds = area.m_collider.bounds;
            bounds.center = area.lastPosition;

            var removed = AreaTreeByType[index].Remove(area, bounds);
            if (!removed)
            {
                AreaTreeByType[index].Remove(area);
            }
        }

        private static int GetIndexFromType(Type type)
        {
            var typeValue = (int)type;
            if (typeValue > 512) return EffectAreaTypeCount - 1;

            // it seems like bed warmth sources can have > 1 type, this one is Heat+Fire
            // TODO: do this properly if someone encounters an issue with this
            if (typeValue == 3) typeValue = 1;

            return (int)Math.Log(typeValue, 2);
        }

        private static void ReinsertAllForType(Type type)
        {
            var toRemove = new List<VPOEffectArea>();
            foreach (var area in ChangedTransforms)
            {
                if ((area.m_type & type) == 0) continue;

                if (area != null && area.m_collider != null)
                {
                    var index = GetIndexFromType(type);

                    Profiler.BeginSample("removin");
                    RemoveAreaWithIndex(index, area);
                    Profiler.EndSample();

                    Profiler.BeginSample("insertin");
                    InsertAreaWithIndex(index, area);
                    Profiler.EndSample();
                }

                toRemove.Add(area);
            }

            toRemove.ForEach(area => ChangedTransforms.Remove(area));
        }

        [HarmonyPatch(typeof(EffectArea), nameof(EffectArea.IsPointInsideArea)), HarmonyPrefix]
        private static bool IsPointInsideArea(Vector3 p, Type type, out EffectArea __result, float radius = 0f)
        {
            if (!_areaTreeInitialized)
            {
                __result = null;
                return false;
            }

            Profiler.BeginSample("reinsertion");
            ReinsertAllForType(type);
            Profiler.EndSample();

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

        [HarmonyPatch(typeof(Game), nameof(Game.Shutdown)), HarmonyPostfix]
        private static void Game_Shutdown_Postfix(Game __instance)
        {
            m_characterMask = 0;
            _areaTreeInitialized = false;
            ChangedTransforms.Clear();
        }
    }

    internal class RequiredEffectAreaFields
    {
        public readonly EffectArea.Type EffectAreaType;
        public readonly string StatusEffect;

        public RequiredEffectAreaFields(string statusEffect, EffectArea.Type effectAreaType)
        {
            StatusEffect = statusEffect;
            EffectAreaType = effectAreaType;
        }
    }
}