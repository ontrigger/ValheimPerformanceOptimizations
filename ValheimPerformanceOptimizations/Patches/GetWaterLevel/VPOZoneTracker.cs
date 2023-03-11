using System.Collections.Generic;
using UnityEngine;

namespace ValheimPerformanceOptimizations.Patches.GetWaterLevel
{
    /// <summary>
    ///     Cache components for each zone
    /// </summary>
    public class VPOZoneTracker : MonoBehaviour
    {
        private static readonly Dictionary<Vector2i, CachedZoneComponents> ZoneComponentsByLocation = new();

        public Vector2i zonePosition;

        private void Awake()
        {
            zonePosition = ZoneSystem.instance.GetZone(transform.position);

            var cachedData = new CachedZoneComponents
            {
                Heightmap = GetComponentInChildren<Heightmap>(),
                WaterVolume = GetComponentInChildren<WaterVolume>(),
            };

            // temp workaround for EpicLoot spawning zones in Client mode
            if (!ZoneComponentsByLocation.ContainsKey(zonePosition))
            {
                ZoneComponentsByLocation.Add(zonePosition, cachedData);
            }
        }

        private void OnDestroy()
        {
            ZoneComponentsByLocation.Remove(zonePosition);
        }

        public static bool GetZoneComponents(Vector3 worldPos, out CachedZoneComponents components)
        {
            var zonePosition = ZoneSystem.instance.GetZone(worldPos);

            return ZoneComponentsByLocation.TryGetValue(zonePosition, out components);
        }

        public class CachedZoneComponents
        {
            public Heightmap Heightmap;
            public WaterVolume WaterVolume;
        }
    }
}