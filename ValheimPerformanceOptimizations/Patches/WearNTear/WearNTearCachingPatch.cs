using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.Profiling;

namespace ValheimPerformanceOptimizations.Patches
{
    [HarmonyPatch]
    public class WearNTearCachingPatch
    {
        private static int _maskToCheck;

        private static readonly HashSet<string> StaticPrefabs = new HashSet<string>();

        private static readonly Dictionary<string, Bounds> MaxBoundsForPrefab = new Dictionary<string, Bounds>();

        private static readonly BoundsOctree<WearNTear> WearNTearTree =
            new BoundsOctree<WearNTear>(4f, Vector3.zero, 2f, 1);

        private static readonly Dictionary<WearNTear, CachedWearNTearData> WearNTearCache
            = new Dictionary<WearNTear, CachedWearNTearData>();

        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake)), HarmonyPostfix]
        private static void Postfix(ZNetScene __instance)
        {
            if (_maskToCheck == 0)
            {
                _maskToCheck = LayerMask.GetMask("piece", "Default", "static_solid", "Default_small", "terrain");
            }

            if (ModCompatibility.IsJotunnPresent)
            {
                ModCompatibility.JotunnPrefabsRegisteredEvent += RegisterPrefabs;
            }
            else
            {
                RegisterPrefabs();
            }
        }

        private static void RegisterPrefabs()
        {
            ZNetScene.instance.m_prefabs.ForEach(prefab =>
            {
                var objectLayer = prefab.gameObject.layer;
                var hasAnyOfTheLayers = _maskToCheck == (_maskToCheck | (1 << objectLayer));

                if (!hasAnyOfTheLayers) return;

                var netView = prefab.GetComponent<ZNetView>();
                if (netView == null) return;

                if (prefab.GetComponent<Character>() || prefab.GetComponent<Pickable>()) return;

                if (!prefab.GetComponentInChildren<Collider>()) return;

                /*ValheimPerformanceOptimizations.Logger.LogInfo(
                    prefab.name
                    + " " + LayerMask.LayerToName(objectLayer) + " "
                    + prefab.GetComponentInChildren<Rigidbody>()
                );*/

                var maxPossibleBounds = prefab.GetComponentsInChildren<Collider>(true)
                    .Where(collider => !collider.isTrigger && collider.attachedRigidbody == null)
                    .Select(collider => collider.bounds)
                    .Aggregate((accBounds, bounds) =>
                    {
                        accBounds.Encapsulate(bounds);
                        return accBounds;
                    });

                MaxBoundsForPrefab[prefab.name] = maxPossibleBounds;

                StaticPrefabs.Add(prefab.name);
            });
        }

        [HarmonyPatch(typeof(ZNetView), nameof(ZNetView.Awake)), HarmonyPostfix]
        private static void Postfix(ZNetView __instance)
        {
            if (ZNetView.m_ghostInit || __instance == null) return;

            var prefabName = ZNetViewPrefabNamePatch.PrefabNameHack ?? __instance.GetPrefabName();

            if (!StaticPrefabs.Contains(prefabName)) return;

            if (!MaxBoundsForPrefab.TryGetValue(prefabName, out var maxBounds)) return;

            Profiler.BeginSample("check overlap");
            maxBounds.center = __instance.transform.position;
            var overlapList = new List<WearNTear>();
            WearNTearTree.GetOverlappingXZ(overlapList, maxBounds);
            Profiler.EndSample();

            ClearWearNTearCaches(overlapList);
        }

        private static void ClearWearNTearCaches(List<WearNTear> toClear)
        {
            toClear.ForEach(wearNTear => WearNTearCache.Remove(wearNTear));
        }

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Awake)), HarmonyPostfix]
        private static void WearNTear_Awake_Postfix(WearNTear __instance)
        {
            var objName = ZNetViewPrefabNamePatch.PrefabNameHack ?? Utils.GetPrefabName(__instance.gameObject);
            if (!MaxBoundsForPrefab.TryGetValue(objName, out var maxBounds)) return;

            maxBounds.center = __instance.transform.position;

            WearNTearTree.Add(__instance.GetComponentInChildren<WearNTear>(), maxBounds);
        }

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.OnDestroy)), HarmonyPostfix]
        private static void WearNTear_OnDestroy_Postfix(WearNTear __instance)
        {
            var objName = Utils.GetPrefabName(__instance.gameObject);
            if (!MaxBoundsForPrefab.TryGetValue(objName, out var maxBounds)) return;

            maxBounds.center = __instance.transform.position;

            var removed = WearNTearTree.Remove(__instance, maxBounds);
            if (!removed)
            {
                removed = WearNTearTree.Remove(__instance);
                ValheimPerformanceOptimizations.Logger.LogInfo(
                    $"Why {removed} {__instance.name} {__instance.transform.position}");
            }

            WearNTearCache.Remove(__instance);
        }

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.HaveRoof)), HarmonyPrefix]
        private static bool HaveRoof(WearNTear __instance, out bool __result)
        {
            __result = false;
            if (WearNTearCache.TryGetValue(__instance, out var myCache))
            {
                __result = myCache.HaveRoof;
                return false;
            }

            myCache = new CachedWearNTearData();
            var num = Physics.SphereCastNonAlloc(__instance.transform.position, 0.1f, Vector3.up,
                                                 WearNTear.m_raycastHits, 100f, WearNTear.m_rayMask);
            for (var i = 0; i < num; i++)
            {
                var raycastHit = WearNTear.m_raycastHits[i];
                if (!raycastHit.collider.gameObject.CompareTag("leaky"))
                {
                    myCache.HaveRoof = true;
                    __result = true;
                    WearNTearCache[__instance] = myCache;

                    return false;
                }
            }

            myCache.HaveRoof = false;
            WearNTearCache[__instance] = myCache;

            return false;
        }

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.UpdateSupport)), HarmonyPrefix]
        private static bool UpdateSupport(WearNTear __instance)
        {
            if (__instance.m_colliders == null)
            {
                __instance.SetupColliders();
            }

            __instance.GetMaterialProperties(out var maxSupport, out var _, out var horizontalLoss,
                                             out var verticalLoss);
            WearNTear.m_tempSupportPoints.Clear();
            WearNTear.m_tempSupportPointValues.Clear();
            var cOM = __instance.GetCOM();
            var a = 0f;

            Profiler.BeginSample("HELLO?");

            if (!WearNTearCache.TryGetValue(__instance, out var myCache))
            {
                myCache = new CachedWearNTearData();
            }

            if (myCache.SupportCached)
            {
                __instance.m_support = myCache.Support;
                __instance.m_nview.GetZDO().Set("support", __instance.m_support);

                Profiler.EndSample();
                return false;
            }

            Profiler.EndSample();

            Profiler.BeginSample("yooo");

            var boundCount = 0;
            var what = 0;
            foreach (var bound in __instance.m_bounds)
            {
                Profiler.BeginSample("get support");
                var colliderSupports = myCache.GetOrComputeColliderSupportData(
                    bound, collider =>
                        !__instance.m_colliders.Contains(collider)
                        && collider.attachedRigidbody == null
                        && !collider.isTrigger
                );
                Profiler.EndSample();

                foreach (var colliderSupportData in colliderSupports)
                {
                    var otherWearNTear = colliderSupportData.WearNTear;
                    if (otherWearNTear == null)
                    {
                        __instance.m_support = maxSupport;
                        __instance.m_nview.GetZDO().Set("support", __instance.m_support);

                        myCache.Support = maxSupport;
                        myCache.ColliderSupportsForBound[bound] = colliderSupports;
                        WearNTearCache[__instance] = myCache;

                        Profiler.EndSample();
                        return false;
                    }

                    if (!otherWearNTear.m_supports) {continue; }

                    boundCount += 1;

                    Profiler.BeginSample("inner loop");

                    var num2 = Vector3.Distance(cOM, otherWearNTear.transform.position) + 0.1f;
                    Profiler.BeginSample("HOW");
                    var support = otherWearNTear.GetSupport();
                    Profiler.EndSample();
                    a = Mathf.Max(a, support - horizontalLoss * num2 * support);

                    if (colliderSupportData.OtherWNTSupport >= 0 
                        && Math.Abs(colliderSupportData.OtherWNTSupport - support) < 0.0001)
                    {
                        what += 1;
                    }

                    colliderSupportData.OtherWNTSupport = support;

                    Profiler.BeginSample("find support");
                    var vector = colliderSupportData.SupportPoint != default(Vector3)
                        ? colliderSupportData.SupportPoint
                        : __instance.FindSupportPoint(cOM, otherWearNTear, colliderSupportData.Collider);
                    Profiler.EndSample();

                    colliderSupportData.SupportPoint = vector;

                    Profiler.BeginSample("actually computin it");
                    if (vector.y < cOM.y + 0.05f)
                    {
                        var normalized = (vector - cOM).normalized;
                        if (normalized.y < 0f)
                        {
                            var t = Mathf.Acos(1f - Mathf.Abs(normalized.y)) / ((float)Math.PI / 2f);
                            var num3 = Mathf.Lerp(horizontalLoss, verticalLoss, t);
                            var b = support - num3 * num2 * support;
                            a = Mathf.Max(a, b);
                        }

                        var item = support - verticalLoss * num2 * support;
                        WearNTear.m_tempSupportPoints.Add(vector);
                        WearNTear.m_tempSupportPointValues.Add(item);
                    }

                    Profiler.EndSample();

                    Profiler.EndSample();
                }

                myCache.ColliderSupportsForBound[bound] = colliderSupports;
            }

            Profiler.EndSample();

            if (WearNTear.m_tempSupportPoints.Count >= 2)
            {
                Profiler.BeginSample("super computin");

                for (var j = 0; j < WearNTear.m_tempSupportPoints.Count; j++)
                {
                    var from = WearNTear.m_tempSupportPoints[j] - cOM;
                    from.y = 0f;
                    for (var k = 0; k < WearNTear.m_tempSupportPoints.Count; k++)
                    {
                        if (j != k)
                        {
                            var to = WearNTear.m_tempSupportPoints[k] - cOM;
                            to.y = 0f;
                            if (Vector3.Angle(from, to) >= 100f)
                            {
                                var b2 = (WearNTear.m_tempSupportPointValues[j] +
                                          WearNTear.m_tempSupportPointValues[k]) * 0.5f;
                                a = Mathf.Max(a, b2);
                            }
                        }
                    }
                }
                Profiler.EndSample();
            }

            __instance.m_support = Mathf.Min(a, maxSupport);
            __instance.m_nview.GetZDO().Set("support", __instance.m_support);
            
            /*if (what >= boundCount && myCache.SupportCached)
            {
                if (Math.Abs(myCache.Support - __instance.m_support) > 0.0001)
                {
                    ValheimPerformanceOptimizations.Logger.LogInfo($"why {myCache.Support} {__instance.m_support}");
                }
                ValheimPerformanceOptimizations.Logger.LogInfo($"less go {boundCount}");
            }*/

            myCache.Support = Mathf.Min(a, maxSupport);

            WearNTearCache[__instance] = myCache;
            
            return false;
        }
    }

    public class CachedWearNTearData
    {
        public readonly Dictionary<WearNTear.BoundData, List<ColliderSupportData>> ColliderSupportsForBound =
            new Dictionary<WearNTear.BoundData, List<ColliderSupportData>>(new BoundDataComparer());

        private float support = -1f;

        public float Support
        {
            get => support;
            set
            {
                support = value;
                SupportCached = true;
            }
        }

        public bool SupportCached { get; private set; }

        public bool HaveRoof { get; set; }

        public List<ColliderSupportData> GetOrComputeColliderSupportData(
            WearNTear.BoundData boundData, Predicate<Collider> colliderPredicate)
        {
            if (ColliderSupportsForBound.TryGetValue(boundData, out var colliderSupports))
            {
                return colliderSupports;
            }

            colliderSupports = new List<ColliderSupportData>();

            var num = Physics.OverlapBoxNonAlloc(boundData.m_pos, boundData.m_size,
                                                 WearNTear.m_tempColliders, boundData.m_rot, WearNTear.m_rayMask);
            for (var i = 0; i < num; i++)
            {
                var collider = WearNTear.m_tempColliders[i];
                if (!colliderPredicate(collider)) continue;

                var wearNTear = collider.GetComponentInParent<WearNTear>();
                var colliderSupportData = new ColliderSupportData
                {
                    WearNTear = wearNTear,
                    Collider = collider
                };

                colliderSupports.Add(colliderSupportData);
            }
            
            return colliderSupports;
        }
    }

    public class ColliderSupportData
    {
        public Collider Collider;
        public float OtherWNTSupport = -1f;

        public Vector3 SupportPoint;

        [CanBeNull]
        public WearNTear WearNTear;
    }

    internal class BoundDataComparer : EqualityComparer<WearNTear.BoundData>
    {
        public override bool Equals(WearNTear.BoundData b1, WearNTear.BoundData b2)
        {
            return b1.m_pos.Equals(b2.m_pos)
                   && b1.m_rot.Equals(b2.m_rot)
                   && b1.m_size.Equals(b2.m_size);
        }

        public override int GetHashCode(WearNTear.BoundData boundData)
        {
            unchecked
            {
                var hashCode = boundData.m_pos.GetHashCode();
                hashCode = (hashCode * 397) ^ boundData.m_rot.GetHashCode();
                hashCode = (hashCode * 397) ^ boundData.m_size.GetHashCode();
                return hashCode;
            }
        }
    }
}