using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace ValheimPerformanceOptimizations.Patches
{
    public class VPOSmokeSpawner : SmokeSpawner
    {
        public readonly List<Smoke> SmokeInstances = new List<Smoke>();

        public static readonly List<VPOSmokeSpawner> AllSmokeSpawners = new List<VPOSmokeSpawner>();

        public static readonly List<Smoke> FreeSmoke = new List<Smoke>();
        
        public static event Action<VPOSmokeSpawner> SpawnerAdded;
        public static event Action<VPOSmokeSpawner> SpawnerDestroyed;

        public static GameObject SmokePrefab;

        private void Awake()
        {
            m_smokePrefab = SmokePrefab;
            
            AllSmokeSpawners.Add(this);
            SpawnerAdded?.Invoke(this);
        }

        private new void Start()
        {
            m_time = Random.Range(0f, m_interval);
        }

        private void OnDestroy()
        {
            FreeSmoke.AddRange(SmokeInstances);

            AllSmokeSpawners.Remove(this);
            SpawnerDestroyed?.Invoke(this);
        }

        private new void Update()
        {
            m_time += Time.deltaTime;
            if (m_time > m_interval)
            {
                m_time = 0f;
                Spawn();
            }
        }

        private new void Spawn()
        {
            var localPlayer = Player.m_localPlayer;
            if (localPlayer == null || Vector3.Distance(localPlayer.transform.position, transform.position) > 64f)
            {
                m_lastSpawnTime = Time.time;
            }
            else if (!TestBlocked())
            {
                if (Smoke.GetTotalSmoke() > 100)
                {
                    Smoke.FadeOldest();
                }

                var smokeObject = Instantiate(m_smokePrefab, transform.position, Random.rotation);
                SmokeInstances.Add(smokeObject.GetComponent<Smoke>());
                m_lastSpawnTime = Time.time;
            }
        }
    }
}