using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;

/*
 *   
Copyright (c) 2014, Nition
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

namespace ValheimPerformanceOptimizations
{
    public class BoundsOctree<T>
    {
        // The total amount of objects currently in the tree
        public int Count { get; private set; }

        // Root node of the octree
        private BoundsOctreeNode<T> rootNode;

        // Should be a value between 1 and 2. A multiplier for the base size of a node.
        // 1.0 is a "normal" octree, while values > 1 have overlap
        private readonly float looseness;

        // Size that the octree was on creation
        private readonly float initialSize;

        // Minimum side length that a node can be - essentially an alternative to having a max depth
        private readonly float minSize;

        // For collision visualisation. Automatically removed in builds.

        /// <summary>
        /// Constructor for the bounds octree.
        /// </summary>
        /// <param name="initialWorldSize">Size of the sides of the initial node, in metres. The octree will never shrink smaller than this.</param>
        /// <param name="initialWorldPos">Position of the centre of the initial node.</param>
        /// <param name="minNodeSize">Nodes will stop splitting if the new nodes would be smaller than this (metres).</param>
        /// <param name="loosenessVal">Clamped between 1 and 2. Values > 1 let nodes overlap.</param>
        /// <param name="comparer"></param>
        public BoundsOctree(float initialWorldSize, Vector3 initialWorldPos, float minNodeSize, float loosenessVal, IEqualityComparer<T> comparer = null)
        {
            if (minNodeSize > initialWorldSize)
            {
                Debug.LogWarning("Minimum node size must be at least as big as the initial world size. Was: " +
                                 minNodeSize + " Adjusted to: " + initialWorldSize);
                minNodeSize = initialWorldSize;
            }

            Count = 0;
            initialSize = initialWorldSize;
            minSize = minNodeSize;
            looseness = Mathf.Clamp(loosenessVal, 1.0f, 2.0f);
            rootNode = new BoundsOctreeNode<T>(initialSize, minSize, looseness, initialWorldPos, comparer);
        }
        

        // #### PUBLIC METHODS ####

        /// <summary>
        /// Add an object.
        /// </summary>
        /// <param name="obj">Object to add.</param>
        /// <param name="objBounds">3D bounding box around the object.</param>
        public void Add(T obj, Bounds objBounds)
        {
            // Add object or expand the octree until it can be added
            int count = 0; // Safety check against infinite/excessive growth
            while (!rootNode.Add(obj, objBounds))
            {
                Grow(objBounds.center - rootNode.Center);
                if (++count > 20)
                {
                    Debug.LogError("Aborted Add operation as it seemed to be going on forever (" + (count - 1) +
                                   ") attempts at growing the octree.");
                    return;
                }
            }

            Count++;
        }

        /// <summary>
        /// Remove an object. Makes the assumption that the object only exists once in the tree.
        /// </summary>
        /// <param name="obj">Object to remove.</param>
        /// <returns>True if the object was removed successfully.</returns>
        public bool Remove(T obj)
        {
            bool removed = rootNode.Remove(obj);

            // See if we can shrink the octree down now that we've removed the item
            if (removed)
            {
                Count--;
                Shrink();
            }

            return removed;
        }

        /// <summary>
        /// Removes the specified object at the given position. Makes the assumption that the object only exists once in the tree.
        /// </summary>
        /// <param name="obj">Object to remove.</param>
        /// <param name="objBounds">3D bounding box around the object.</param>
        /// <returns>True if the object was removed successfully.</returns>
        public bool Remove(T obj, Bounds objBounds)
        {
            bool removed = rootNode.Remove(obj, objBounds);

			Profiler.BeginSample("shrinking");
            // See if we can shrink the octree down now that we've removed the item
            if (removed)
            {
                Count--;
                Shrink();
            }
			Profiler.EndSample();

            return removed;
        }

        /// <summary>
        /// Check if the specified bounds intersect with anything in the tree. See also: GetColliding.
        /// </summary>
        /// <param name="checkBounds">bounds to check.</param>
        /// <returns>True if there was a collision.</returns>
        public bool IsColliding(Bounds checkBounds)
        {
            //#if UNITY_EDITOR
            // For debugging
            //AddCollisionCheck(checkBounds);
            //#endif
            return rootNode.IsColliding(ref checkBounds);
        }

        /// <summary>
        /// Check if the specified ray intersects with anything in the tree. See also: GetColliding.
        /// </summary>
        /// <param name="checkRay">ray to check.</param>
        /// <param name="maxDistance">distance to check.</param>
        /// <returns>True if there was a collision.</returns>
        public bool IsColliding(Ray checkRay, float maxDistance)
        {
            //#if UNITY_EDITOR
            // For debugging
            //AddCollisionCheck(checkRay);
            //#endif
            return rootNode.IsColliding(ref checkRay, maxDistance);
        }

        /// <summary>
        /// Returns an array of objects that intersect with the specified bounds, if any. Otherwise returns an empty array. See also: IsColliding.
        /// </summary>
        /// <param name="collidingWith">list to store intersections.</param>
        /// <param name="checkBounds">bounds to check.</param>
        /// <returns>Objects that intersect with the specified bounds.</returns>
        public void GetOverlapping(List<T> collidingWith, Bounds checkBounds)
        {
            rootNode.GetOverlapping(ref checkBounds, collidingWith);
        }
        
        public void GetOverlapping(List<T> collidingWith, Vector3 center, float radius)
        {
            rootNode.GetOverlapping(center, radius, collidingWith);
        }

        public void GetOverlappingXZ(List<T> collidingWith, Bounds checkBounds)
        {
            rootNode.GetOverlappingXZ(ref checkBounds, collidingWith);
        }

        /// <summary>
        /// Returns an array of objects that intersect with the specified ray, if any. Otherwise returns an empty array. See also: IsColliding.
        /// </summary>
        /// <param name="collidingWith">list to store intersections.</param>
        /// <param name="checkRay">ray to check.</param>
        /// <param name="maxDistance">distance to check.</param>
        /// <returns>Objects that intersect with the specified ray.</returns>
        public void GetOverlapping(List<T> collidingWith, Ray checkRay, float maxDistance = float.PositiveInfinity)
        {
            //#if UNITY_EDITOR
            // For debugging
            //AddCollisionCheck(checkRay);
            //#endif
            rootNode.GetColliding(ref checkRay, collidingWith, maxDistance);
        }

        public List<T> GetWithinFrustum(Camera cam)
        {
            var planes = GeometryUtility.CalculateFrustumPlanes(cam);

            var list = new List<T>();
            rootNode.GetWithinFrustum(planes, list);
            return list;
        }

        public Bounds GetMaxBounds()
        {
            return rootNode.GetBounds();
        }

        /// <summary>
        /// Draws node boundaries visually for debugging.
        /// Must be called from OnDrawGizmos externally. See also: DrawAllObjects.
        /// </summary>
        public void DrawAllBounds()
        {
            rootNode.DrawAllBounds();
        }

        /// <summary>
        /// Draws the bounds of all objects in the tree visually for debugging.
        /// Must be called from OnDrawGizmos externally. See also: DrawAllBounds.
        /// </summary>
        public void DrawAllObjects()
        {
            rootNode.DrawAllObjects();
        }

        // Intended for debugging. Must be called from OnDrawGizmos externally
        // See also DrawAllBounds and DrawAllObjects
        /// <summary>
        /// Visualises collision checks from IsColliding and GetColliding.
        /// Collision visualisation code is automatically removed from builds so that collision checks aren't slowed down.
        /// </summary>
#if UNITY_EDITOR
	public void DrawCollisionChecks() {
		int count = 0;
		foreach (Bounds collisionCheck in lastBoundsCollisionChecks) {
			Gizmos.color = new Color(1.0f, 1.0f - ((float)count / numCollisionsToSave), 1.0f);
			Gizmos.DrawCube(collisionCheck.center, collisionCheck.size);
			count++;
		}

		foreach (Ray collisionCheck in lastRayCollisionChecks) {
			Gizmos.color = new Color(1.0f, 1.0f - ((float)count / numCollisionsToSave), 1.0f);
			Gizmos.DrawRay(collisionCheck.origin, collisionCheck.direction);
			count++;
		}
		Gizmos.color = Color.white;
	}
#endif

        // #### PRIVATE METHODS ####

        /// <summary>
        /// Used for visualising collision checks with DrawCollisionChecks.
        /// Automatically removed from builds so that collision checks aren't slowed down.
        /// </summary>
        /// <param name="checkBounds">bounds that were passed in to check for collisions.</param>
#if UNITY_EDITOR
	void AddCollisionCheck(Bounds checkBounds) {
		lastBoundsCollisionChecks.Enqueue(checkBounds);
		if (lastBoundsCollisionChecks.Count > numCollisionsToSave) {
			lastBoundsCollisionChecks.Dequeue();
		}
	}
#endif

        /// <summary>
        /// Used for visualising collision checks with DrawCollisionChecks.
        /// Automatically removed from builds so that collision checks aren't slowed down.
        /// </summary>
        /// <param name="checkRay">ray that was passed in to check for collisions.</param>
#if UNITY_EDITOR
	void AddCollisionCheck(Ray checkRay) {
		lastRayCollisionChecks.Enqueue(checkRay);
		if (lastRayCollisionChecks.Count > numCollisionsToSave) {
			lastRayCollisionChecks.Dequeue();
		}
	}
#endif

        /// <summary>
        /// Grow the octree to fit in all objects.
        /// </summary>
        /// <param name="direction">Direction to grow.</param>
        void Grow(Vector3 direction)
        {
            int xDirection = direction.x >= 0 ? 1 : -1;
            int yDirection = direction.y >= 0 ? 1 : -1;
            int zDirection = direction.z >= 0 ? 1 : -1;
            BoundsOctreeNode<T> oldRoot = rootNode;
            float half = rootNode.BaseLength / 2;
            float newLength = rootNode.BaseLength * 2;
            Vector3 newCenter = rootNode.Center + new Vector3(xDirection * half, yDirection * half, zDirection * half);

            // Create a new, bigger octree root node
            rootNode = new BoundsOctreeNode<T>(newLength, minSize, looseness, newCenter);

            if (oldRoot.HasAnyObjects())
            {
                // Create 7 new octree children to go with the old root as children of the new root
                int rootPos = rootNode.BestFitChild(oldRoot.Center);
                BoundsOctreeNode<T>[] children = new BoundsOctreeNode<T>[8];
                for (int i = 0; i < 8; i++)
                {
                    if (i == rootPos)
                    {
                        children[i] = oldRoot;
                    }
                    else
                    {
                        xDirection = i % 2 == 0 ? -1 : 1;
                        yDirection = i > 3 ? -1 : 1;
                        zDirection = (i < 2 || (i > 3 && i < 6)) ? -1 : 1;
                        children[i] = new BoundsOctreeNode<T>(oldRoot.BaseLength, minSize, looseness,
                                                              newCenter + new Vector3(
                                                                  xDirection * half, yDirection * half,
                                                                  zDirection * half));
                    }
                }

                // Attach the new children to the new root node
                rootNode.SetChildren(children);
            }
        }

        /// <summary>
        /// Shrink the octree if possible, else leave it the same.
        /// </summary>
        void Shrink()
        {
            rootNode = rootNode.ShrinkIfPossible(initialSize);
        }
    }
}