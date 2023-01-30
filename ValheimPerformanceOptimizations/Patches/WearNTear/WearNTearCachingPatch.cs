using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.Profiling;
using ValheimPerformanceOptimizations.Extensions;

namespace ValheimPerformanceOptimizations.Patches
{
    public static class WearNTearCachingPatch
    {
        private static int _maskToCheck;

        private static readonly Dictionary<string, Bounds> MaxBoundsForPrefab = new Dictionary<string, Bounds>();

        private static BoundsOctree<int> WearNTearIdTree;

        private static readonly Dictionary<int, CachedWearNTearData> WearNTearCache
            = new Dictionary<int, CachedWearNTearData>();

        private static readonly MethodInfo HaveRoofMethod
            = AccessTools.DeclaredMethod(typeof(WearNTear), nameof(WearNTear.HaveRoof));

        private static readonly MethodInfo GetEnvManInstanceMethod
            = AccessTools.DeclaredMethod(typeof(EnvMan), "get_instance");

        private static readonly MethodInfo IsWetMethod
            = AccessTools.DeclaredMethod(typeof(EnvMan), nameof(EnvMan.IsWet));

        static WearNTearCachingPatch()
        {
            ValheimPerformanceOptimizations.OnInitialized += Initialize;
        }

        private static void Initialize(ConfigFile configFile, Harmony harmony)
        {
            if (ModCompatibility.IsValheimRaftPresent)
            {
                ValheimPerformanceOptimizations.Logger.LogWarning(
                    "!!! ValheimRAFT present !!! disabling structural integrity optimizations to maintain compatibility");
            }
            else
            {
                harmony.PatchAll(typeof(WearNTearCachingPatch));
            }
        }

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

            var refPos = ZNet.instance.GetReferencePosition();
            WearNTearIdTree = new BoundsOctree<int>(192f, refPos, 4f, 1f);
        }

        private static void RegisterPrefabs()
        {
            ZNetScene.instance.m_prefabs.ForEach(prefab =>
            {
                var objectLayer = prefab.gameObject.layer;
                var hasAnyOfTheLayers = _maskToCheck == (_maskToCheck | (1 << objectLayer));

                if (!hasAnyOfTheLayers) return;

                var netView = prefab.GetComponent<ZNetView>();
                if (netView == null) { return; }

                if (prefab.GetComponent<Character>() || prefab.GetComponent<Pickable>()) return;

                if (!prefab.GetComponentInChildren<Collider>()) return;

                var maxPossibleBounds = prefab.GetComponentsInChildren<Collider>(true)
                    .Where(collider => !collider.isTrigger && collider.attachedRigidbody == null)
                    .Aggregate(new Bounds(), (accBounds, collider) =>
                    {
                        Vector3 size;
                        switch (collider)
                        {
                            case BoxCollider boxCollider:
                            {
                                var lossyScale = boxCollider.transform.lossyScale;
                                size = boxCollider.size;
                                var actualSize = new Vector3(lossyScale.x * size.x, lossyScale.y * size.y,
                                                             lossyScale.z * size.z);

                                accBounds.Encapsulate(new Bounds(Vector3.zero, actualSize));
                                return accBounds;
                            }
                            case SphereCollider sphereCollider:
                                size = sphereCollider.radius * 2 * Vector3.one;

                                accBounds.Encapsulate(new Bounds(Vector3.zero, size));
                                return accBounds;
                            default:
                            {
                                var bounds = collider.bounds;
                                bounds.center = Vector3.zero;

                                accBounds.Encapsulate(bounds);
                                return accBounds;
                            }
                        }
                    });


                MaxBoundsForPrefab[prefab.name] = maxPossibleBounds;
            });
        }

        [HarmonyPatch(typeof(ZNetView), nameof(ZNetView.Awake)), HarmonyPostfix]
        private static void Postfix(ZNetView __instance)
        {
            if (ZNetView.m_ghostInit || __instance == null) return;

            var prefabName = ZNetViewPrefabNamePatch.PrefabNameHack ?? __instance.GetPrefabName();

            if (!MaxBoundsForPrefab.TryGetValue(prefabName, out var maxBounds)) return;

            maxBounds.center = __instance.transform.position;

            Profiler.BeginSample("check overlap");
            var overlapList = new List<int>();
            WearNTearIdTree.GetOverlappingXZ(overlapList, maxBounds);
            Profiler.EndSample();

            ClearWearNTearCaches(overlapList);
        }

        private static void ClearWearNTearCaches(List<int> toClear)
        {
            toClear.ForEach(wearNTearId => WearNTearCache.Remove(wearNTearId));
        }

        [HarmonyPatch(typeof(Game), nameof(Game.Shutdown)), HarmonyPostfix]
        private static void Game_Shutdown_Postfix(Game __instance)
        {
            WearNTearCache.Clear();
            //WearNTearIdTree = null;
        }

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Awake)), HarmonyPostfix]
        private static void WearNTear_Awake_Postfix(WearNTear __instance)
        {
            var objName = ZNetViewPrefabNamePatch.PrefabNameHack ?? Utils.GetPrefabName(__instance.gameObject);
            if (!MaxBoundsForPrefab.TryGetValue(objName, out var maxBounds)) return;

            maxBounds.center = __instance.transform.position;

            WearNTearIdTree.Add(__instance.GetInstanceID(), maxBounds);
        }

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.OnDestroy)), HarmonyPostfix]
        private static void WearNTear_OnDestroy_Postfix(WearNTear __instance)
        {
            var objName = __instance.gameObject.name;
            if (!MaxBoundsForPrefab.TryGetValue(objName, out var maxBounds)) return;

            maxBounds.center = __instance.transform.position;

            Profiler.BeginSample("normal remove");
            var removed = WearNTearIdTree.Remove(__instance.GetInstanceID(), maxBounds);
            if (!removed)
            {
                Profiler.BeginSample("why so slo");
                WearNTearIdTree.Remove(__instance.GetInstanceID());
                Profiler.EndSample();
            }

            Profiler.EndSample();

            Profiler.BeginSample("overlap slow asf");
            if (!ZNetScene.instance.OutsideActiveArea(__instance.transform.position))
            {
                var toClear = new List<int>();
                maxBounds.Expand(0.5f);

                WearNTearIdTree.GetOverlappingXZ(toClear, maxBounds);

                var maxBoundsCenter = maxBounds.center;
                maxBoundsCenter.y += 0.5f;

                ClearWearNTearCaches(toClear);
            }

            Profiler.EndSample();

            Profiler.BeginSample("Cache remove");
            WearNTearCache.Remove(__instance.GetInstanceID());
            Profiler.EndSample();
        }

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.HaveRoof)), HarmonyPrefix]
        private static bool HaveRoof(WearNTear __instance, out bool __result)
        {
            __result = false;
            if (WearNTearCache.TryGetValue(__instance.GetInstanceID(), out var myCache) && myCache.RoofCheckCached)
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
                    WearNTearCache[__instance.GetInstanceID()] = myCache;

                    return false;
                }
            }

            myCache.HaveRoof = false;
            WearNTearCache[__instance.GetInstanceID()] = myCache;

            return false;
        }

        /// <summary>
        /// adds an IsWet check before trying to call HaveRoof like this
        /// bool flag = true;
        /// if (EnvMan.instance.IsWet())
        /// {
        ///     flag = HaveRoof();
        /// }
        /// </summary>
        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.UpdateWear)), HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> WearNTear_UpdateWear_Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var code = new List<CodeInstruction>(instructions);

            var getInstanceLabel = code.FindIndex(c => c.Is(OpCodes.Call, GetEnvManInstanceMethod));
            var getInstanceJumpLabel = generator.DefineLabel();
            code[getInstanceLabel].labels.Add(getInstanceJumpLabel);

            var haveRoofCallIndex = code.FindIndex(c => c.Is(OpCodes.Call, HaveRoofMethod));
            code.RemoveRange(haveRoofCallIndex - 1, 2);

            code.Insert(haveRoofCallIndex - 1, new CodeInstruction(OpCodes.Ldc_I4_1));

            code.Insert(haveRoofCallIndex + 1, new CodeInstruction(OpCodes.Call, GetEnvManInstanceMethod));
            code.Insert(haveRoofCallIndex + 2, new CodeInstruction(OpCodes.Callvirt, IsWetMethod));

            var isWetLocal = generator.DeclareLocal(typeof(bool));
            code.InsertRange(haveRoofCallIndex + 3, new[]
            {
                new CodeInstruction(OpCodes.Stloc_S, isWetLocal.LocalIndex),
                new CodeInstruction(OpCodes.Ldloc_S, isWetLocal.LocalIndex),
                new CodeInstruction(OpCodes.Brfalse_S, getInstanceJumpLabel),
                new CodeInstruction(OpCodes.Nop),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, HaveRoofMethod),
                new CodeInstruction(OpCodes.Stloc_1),
                new CodeInstruction(OpCodes.Nop)
            });

            return code.AsEnumerable();
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

            if (!WearNTearCache.TryGetValue(__instance.GetInstanceID(), out var myCache))
            {
                myCache = new CachedWearNTearData();
            }

            Profiler.EndSample();

            Profiler.BeginSample("yooo");

            var boundCount = 0;
            var unchangedSupports = 0;
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
                        WearNTearCache[__instance.GetInstanceID()] = myCache;

                        Profiler.EndSample();
                        return false;
                    }

                    if (!otherWearNTear.m_supports) { continue; }

                    boundCount += 1;

                    Profiler.BeginSample("inner loop");
					
					Profiler.BeginSample("HOW");
                    var num2 = Vector3.Distance(cOM, otherWearNTear.transform.position) + 0.1f;
                    var support = otherWearNTear.GetSupport();
                    a = Mathf.Max(a, support - horizontalLoss * num2 * support);
					Profiler.EndSample();

					Profiler.BeginSample("nothing else works");
                    if (colliderSupportData.CheckSupportUnchanged(num2, support))
                    {
                        unchangedSupports += 1;
                    }

                    colliderSupportData.OtherWntSupport = support;
                    colliderSupportData.DistanceToOtherWnt = num2;
					
					Profiler.EndSample();

					Profiler.BeginSample("find support");
                    var vector = colliderSupportData.SupportPointCached
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

            if (unchangedSupports >= boundCount)
            {
                __instance.m_support = myCache.Support;
                __instance.m_nview.GetZDO().Set("support", __instance.m_support);

                return false;
            }

            if (WearNTear.m_tempSupportPoints.Count >= 2)
            {
                Profiler.BeginSample("super computin");
                for (int j = 0; j < WearNTear.m_tempSupportPoints.Count; j++)
                {
                    var from = WearNTear.m_tempSupportPoints[j] - cOM;
                    from.y = 0f;
                    for (var k = j + 1; k < WearNTear.m_tempSupportPoints.Count; k++)
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

                Profiler.EndSample();
            }

            __instance.m_support = Mathf.Min(a, maxSupport);
            __instance.m_nview.GetZDO().Set("support", __instance.m_support);

            myCache.Support = Mathf.Min(a, maxSupport);

            WearNTearCache[__instance.GetInstanceID()] = myCache;

            return false;
        }
    }

    public class CachedWearNTearData
    {
        public readonly Dictionary<WearNTear.BoundData, List<ColliderSupportData>> ColliderSupportsForBound =
            new Dictionary<WearNTear.BoundData, List<ColliderSupportData>>(new BoundDataComparer());

        private float support = -1f;
        private bool haveRoof;

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

        public bool HaveRoof
        {
            get => haveRoof;
            set
            {
                haveRoof = value;
                RoofCheckCached = true;
            }
        }

        public bool RoofCheckCached { get; private set; }

        public List<ColliderSupportData> GetOrComputeColliderSupportData(
            WearNTear.BoundData boundData, Predicate<Collider> colliderPredicate)
        {
            if (ColliderSupportsForBound.TryGetValue(boundData, out var colliderSupports))
            {
                return colliderSupports;
            }
            
            var num = Physics.OverlapBoxNonAlloc(boundData.m_pos, boundData.m_size,
                                                 WearNTear.m_tempColliders, boundData.m_rot, WearNTear.m_rayMask);
            
            colliderSupports = new List<ColliderSupportData>(num);
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

        public float OtherWntSupport { get; set; } = -1f;
        public float DistanceToOtherWnt { get; set; } = -1f;

        [CanBeNull]
        public WearNTear WearNTear;

        public Vector3 SupportPoint
        {
            get => supportPoint;
            set
            {
                supportPoint = value;
                SupportPointCached = true;
            }
        }

        public bool SupportPointCached { get; private set; }

        private Vector3 supportPoint;

        public bool CheckSupportUnchanged(float distanceToWnt, float otherWntSupport)
        {
            if (OtherWntSupport < 0 || DistanceToOtherWnt < 0) { return false; }

            return DistanceToOtherWnt.IsNearlyEqual(distanceToWnt)
                   && OtherWntSupport.IsNearlyEqual(otherWntSupport);
        }
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