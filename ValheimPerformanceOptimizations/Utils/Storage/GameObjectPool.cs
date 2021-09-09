using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ValheimPerformanceOptimizations
{
    // TODO: maybe turn it into a component instead
    public class GameObjectPool
    {
        public readonly Stack<GameObject> Pool = new Stack<GameObject>();

        public readonly int MaxObjects;

        public int PooledObjectCount => Pool.Count;

        private readonly Action<GameObject> onRetrieve;
        private readonly Action<GameObject> onReturn;
        
        private readonly GameObject toPool;

        public GameObjectPool(
            GameObject pooledObject, int maxObjects,
            Action<GameObject> onRetrieve = null, Action<GameObject> onReturn = null)
        {
            toPool = pooledObject;
            MaxObjects = maxObjects;
            
            this.onRetrieve = onRetrieve;
            this.onReturn = onReturn;
        }

        public void Populate(int count, Action<GameObject> setupObjectAction)
        {
            for (var i = 0; i < count; i++)
            {
                var obj = Object.Instantiate(toPool);
                obj.SetActive(false);
                
                setupObjectAction?.Invoke(obj);
                
                Pool.Push(obj);
            }
        }

        public GameObject GetObject(Vector3 position, Quaternion rotation)
        {
            GameObject obj;

            do
            {
                if (Pool.Count > 0)
                {
                    obj = Pool.Pop();
                    continue;
                }
                
                return Object.Instantiate(toPool, position, rotation);
            } while (obj == null);

            obj.SetActive(true);

            obj.transform.position = position;
            obj.transform.rotation = rotation;

            onRetrieve?.Invoke(obj);

            return obj;
        }

        public void ReturnObject(GameObject toRelease)
        {
            if (Pool.Count >= MaxObjects)
            {
                Object.Destroy(toRelease);
                return;
            }
            
            onReturn?.Invoke(toRelease);
            
            toRelease.SetActive(false);

            Pool.Push(toRelease);
        }

        public void ReleaseAll()
        {
            foreach (var gameObject in Pool)
            {
                onRetrieve?.Invoke(gameObject);
            }

            Pool.Clear();
        }
    }
}