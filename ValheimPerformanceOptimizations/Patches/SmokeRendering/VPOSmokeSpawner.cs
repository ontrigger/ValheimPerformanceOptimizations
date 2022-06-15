using System;
using UnityEngine;
using Random = UnityEngine.Random;

namespace ValheimPerformanceOptimizations.Patches
{
    public class VPOSmokeSpawner : SmokeSpawner
    {
        public static event Action<VPOSmokeSpawner> SpawnerAdded;
        public static event Action<VPOSmokeSpawner> SpawnerDestroyed;
        public event Action<Smoke> SmokeSpawned;

        public static GameObject SmokePrefab;

        private void Awake()
        {
            m_smokePrefab = SmokePrefab;
            
            SpawnerAdded?.Invoke(this);
        }

        private new void Start()
        {
            m_time = Random.Range(0f, m_interval);
        }

        private void OnDestroy()
        {
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
				SmokeSpawned?.Invoke(smokeObject.GetComponent<Smoke>());
                m_lastSpawnTime = Time.time;
            }
        }
    }
}