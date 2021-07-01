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

        private readonly GameObject toPool;
        public int MaxObjects;

        public Action<GameObject> OnRetrieve;
        public Action<GameObject> OnReturn;

        public GameObjectPool(
            GameObject pooledObject, int maxObjects,
            Action<GameObject> onRetrieve = null, Action<GameObject> onReturn = null)
        {
            toPool = pooledObject;
            MaxObjects = maxObjects;
            OnRetrieve = onRetrieve;
            OnReturn = onReturn;
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
            if (Pool.Count <= 0)
            {
                return Object.Instantiate(toPool, position, rotation);
            }

            var obj = Pool.Pop();
            while (obj == null)
            {
                if (Pool.Count > 0)
                {
                    obj = Pool.Pop();
                }
                else
                {
                    obj = Object.Instantiate(toPool, position, rotation);
                    break;
                }
            }

            obj.SetActive(true);

            obj.transform.position = position;
            obj.transform.rotation = rotation;

            OnRetrieve?.Invoke(obj);

            return obj;
        }

        public void ReturnObject(GameObject toRelease)
        {
            if (Pool.Count >= MaxObjects)
            {
                Object.Destroy(toRelease);
                return;
            }

            toRelease.SetActive(false);
            
            OnReturn?.Invoke(toRelease);
            
            Pool.Push(toRelease);
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