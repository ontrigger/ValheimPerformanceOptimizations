using System.Collections.Generic;
using UnityEngine;

namespace ValheimPerformanceOptimizations
{
    // TODO: maybe turn it into a component instead
    public class GameObjectPool
    {
        public readonly Stack<GameObject> Pool = new Stack<GameObject>();

        private readonly Transform root;
        private readonly GameObject toPool;
        public int MaxObjects;

        public GameObjectPool(GameObject pooledObject, Transform rootTransform, int maxObjects, int prewarmAmount = 0)
        {
            toPool = pooledObject;
            MaxObjects = maxObjects;

            root = rootTransform;

            for (var i = 0; i < prewarmAmount; i++)
            {
                var obj = Object.Instantiate(pooledObject, rootTransform, true);
                obj.SetActive(false);

                Pool.Push(obj);
            }
        }

        public GameObject GetObject(Vector3 position, Quaternion rotation, out bool fromPool)
        {
            if (Pool.Count <= 0)
            {
                fromPool = false;
                return Object.Instantiate(toPool, position, rotation);
            }

            var obj = Pool.Pop();
            obj.SetActive(true);

            obj.transform.SetParent(null);
            obj.transform.position = position;
            obj.transform.rotation = rotation;

            fromPool = true;
            return obj;
        }

        public void ReturnObject(GameObject toRelease, out bool returned)
        {
            if (Pool.Count >= MaxObjects)
            {
                Object.Destroy(toRelease);
                returned = false;
                return;
            }

            if (root)
            {
                toRelease.transform.SetParent(root);
            }

            toRelease.SetActive(false);
            Pool.Push(toRelease);

            returned = true;
        }

        public void Destroy()
        {
            foreach (var gameObject in Pool)
            {
                Object.Destroy(gameObject);
            }

            Pool.Clear();
        }
    }
}