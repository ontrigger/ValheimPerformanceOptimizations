using System.Collections.Generic;
using UnityEngine;

namespace ValheimPerformanceOptimizations
{
    // TODO: maybe turn it into a component instead
    public class GameObjectPool
    {
        private readonly Stack<GameObject> pool = new Stack<GameObject>();

        private readonly GameObject root;
        private readonly GameObject toPool;
        public int MaxObjects;

        public GameObjectPool(GameObject pooledObject, GameObject rootObject, int maxObjects, int prewarmAmount = 0)
        {
            toPool = pooledObject;
            MaxObjects = maxObjects;

            root = rootObject;

            for (var i = 0; i < prewarmAmount; i++)
            {
                var obj = Object.Instantiate(pooledObject, rootObject.transform, true);
                obj.SetActive(false);

                pool.Push(obj);
            }
        }

        public GameObject GetObject(Vector3 position, Quaternion rotation, out bool fromPool)
        {
            if (pool.Count <= 0)
            {
                fromPool = false;
                return Object.Instantiate(toPool, position, rotation);
            }

            var obj = pool.Pop();
            obj.SetActive(true);

            obj.transform.SetParent(null);
            obj.transform.position = position;
            obj.transform.rotation = rotation;

            fromPool = true;
            return obj;
        }

        public void ReturnObject(GameObject toRelease, out bool returned)
        {
            if (pool.Count >= MaxObjects)
            {
                Object.Destroy(toRelease);
                returned = false;
                return;
            }

            toRelease.transform.SetParent(root.transform);
            toRelease.SetActive(false);
            pool.Push(toRelease);

            returned = true;
        }
    }
}