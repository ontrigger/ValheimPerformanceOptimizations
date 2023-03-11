using System.Collections.Generic;
using UnityEngine;
using ValheimPerformanceOptimizations.Extensions;

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
	public class BoundsOctreeNode<T>
	{
		// Centre of this node
		public Vector3 Center { get; private set; }

		// Length of this node if it has a looseness of 1.0
		public float BaseLength { get; private set; }

		// Looseness value for this node
		private float looseness;

		// Minimum size for a node in this octree
		private float minSize;

		// Actual length of sides, taking the looseness value into account
		private float adjLength;

		// Bounding box that represents this node
		private Bounds bounds;

		// Objects in this node
		private readonly List<OctreeObject> objects = new();

		// Child nodes, if any
		private BoundsOctreeNode<T>[] children;

		private bool HasChildren => children != null;

		// Bounds of potential children to this node. These are actual size (with looseness taken into account), not base size
		private Bounds[] childBounds;
		private IEqualityComparer<T> comparator;

		// If there are already NUM_OBJECTS_ALLOWED in a node, we split it into children
		// A generally good number seems to be something around 8-15
		private const int NUM_OBJECTS_ALLOWED = 8;

		// An object in the octree
		private struct OctreeObject
		{
			public T Obj;
			public Bounds Bounds;
		}

		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="baseLengthVal">Length of this node, not taking looseness into account.</param>
		/// <param name="minSizeVal">Minimum size of nodes in this octree.</param>
		/// <param name="loosenessVal">Multiplier for baseLengthVal to get the actual size.</param>
		/// <param name="centerVal">Centre position of this node.</param>
		/// <param name="comparer"></param>
		public BoundsOctreeNode(
			float baseLengthVal, float minSizeVal, float loosenessVal, Vector3 centerVal,
			IEqualityComparer<T> comparer = null)
		{
			SetValues(baseLengthVal, minSizeVal, loosenessVal, centerVal, comparer);
		}

		// #### PUBLIC METHODS ####

		/// <summary>
		/// Add an object.
		/// </summary>
		/// <param name="obj">Object to add.</param>
		/// <param name="objBounds">3D bounding box around the object.</param>
		/// <returns>True if the object fits entirely within this node.</returns>
		public bool Add(T obj, Bounds objBounds)
		{
			if (!Encapsulates(bounds, objBounds))
			{
				return false;
			}

			SubAdd(obj, objBounds);
			return true;
		}

		/// <summary>
		/// Remove an object. Makes the assumption that the object only exists once in the tree.
		/// </summary>
		/// <param name="obj">Object to remove.</param>
		/// <returns>True if the object was removed successfully.</returns>
		public bool Remove(T obj)
		{
			var removed = false;

			for (var i = 0; i < objects.Count; i++)
			{
				if (comparator.Equals(objects[i].Obj, obj))
				{
					objects.RemoveBySwap(i);
					removed = true;
					break;
				}
			}

			if (!removed && children != null)
			{
				for (var i = 0; i < 8; i++)
				{
					removed = children[i].Remove(obj);
					if (removed)
					{
						break;
					}
				}
			}

			if (removed && children != null)
			{
				// Check if we should merge nodes now that we've removed an item
				if (ShouldMerge())
				{
					Merge();
				}
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
			if (!Encapsulates(bounds, objBounds))
			{
				return false;
			}

			return SubRemove(obj, objBounds, false);
		}

		public int RemoveAll(Bounds objBounds)
		{
			if (!Encapsulates(bounds, objBounds))
			{
				return 0;
			}

			return SubRemoveAll(objBounds);
		}

		/// <summary>
		/// Check if the specified bounds intersect with anything in the tree. See also: GetColliding.
		/// </summary>
		/// <param name="checkBounds">Bounds to check.</param>
		/// <returns>True if there was a collision.</returns>
		public bool IsColliding(ref Bounds checkBounds)
		{
			// Are the input bounds at least partially in this node?
			if (!bounds.Intersects(checkBounds))
			{
				return false;
			}

			// Check against any objects in this node
			for (var i = 0; i < objects.Count; i++)
			{
				if (objects[i].Bounds.Intersects(checkBounds))
				{
					return true;
				}
			}

			// Check children
			if (children != null)
			{
				for (var i = 0; i < 8; i++)
				{
					if (children[i].IsColliding(ref checkBounds))
					{
						return true;
					}
				}
			}

			return false;
		}

		/// <summary>
		/// Check if the specified ray intersects with anything in the tree. See also: GetColliding.
		/// </summary>
		/// <param name="checkRay">Ray to check.</param>
		/// <param name="maxDistance">Distance to check.</param>
		/// <returns>True if there was a collision.</returns>
		public bool IsColliding(ref Ray checkRay, float maxDistance = float.PositiveInfinity)
		{
			// Is the input ray at least partially in this node?
			float distance;
			if (!bounds.IntersectRay(checkRay, out distance) || distance > maxDistance)
			{
				return false;
			}

			// Check against any objects in this node
			for (var i = 0; i < objects.Count; i++)
			{
				if (objects[i].Bounds.IntersectRay(checkRay, out distance) && distance <= maxDistance)
				{
					return true;
				}
			}

			// Check children
			if (children != null)
			{
				for (var i = 0; i < 8; i++)
				{
					if (children[i].IsColliding(ref checkRay, maxDistance))
					{
						return true;
					}
				}
			}

			return false;
		}

		/// <summary>
		/// Returns an array of objects that intersect with the specified bounds, if any. Otherwise returns an empty array. See
		/// also: IsColliding.
		/// </summary>
		/// <param name="checkBounds">Bounds to check. Passing by ref as it improves performance with structs.</param>
		/// <param name="result">List result.</param>
		/// <returns>Objects that intersect with the specified bounds.</returns>
		public void GetOverlapping(ref Bounds checkBounds, List<T> result)
		{
			// Are the input bounds at least partially in this node?
			if (!bounds.Intersects(checkBounds))
			{
				return;
			}

			// Check against any objects in this node
			for (var i = 0; i < objects.Count; i++)
			{
				if (objects[i].Bounds.Intersects(checkBounds))
				{
					result.Add(objects[i].Obj);
				}
			}

			// Check children
			if (children != null)
			{
				for (var i = 0; i < 8; i++)
				{
					children[i].GetOverlapping(ref checkBounds, result);
				}
			}
		}

		public void GetOverlapping(Vector3 center, float radius, List<T> collidingWith)
		{
			if (!bounds.IntersectsSphere(center, radius))
			{
				return;
			}

			// Check against any objects in this node
			for (var i = 0; i < objects.Count; i++)
			{
				if (objects[i].Bounds.IntersectsSphere(center, radius))
				{
					collidingWith.Add(objects[i].Obj);
				}
			}

			// Check children
			if (children != null)
			{
				for (var i = 0; i < 8; i++)
				{
					children[i].GetOverlapping(center, radius, collidingWith);
				}
			}
		}

		public void GetOverlappingXZ(ref Bounds checkBounds, List<T> result)
		{
			// Are the input bounds at least partially in this node?
			if (!bounds.IntersectsXZ(checkBounds))
			{
				return;
			}

			// Check against any objects in this node
			for (var i = 0; i < objects.Count; i++)
			{
				if (objects[i].Bounds.IntersectsXZ(checkBounds))
				{
					result.Add(objects[i].Obj);
				}
			}

			// Check children
			if (children != null)
			{
				for (var i = 0; i < 8; i++)
				{
					children[i].GetOverlappingXZ(ref checkBounds, result);
				}
			}
		}

		/// <summary>
		/// Returns an array of objects that intersect with the specified ray, if any. Otherwise returns an empty array. See also:
		/// IsColliding.
		/// </summary>
		/// <param name="checkRay">Ray to check. Passing by ref as it improves performance with structs.</param>
		/// <param name="maxDistance">Distance to check.</param>
		/// <param name="result">List result.</param>
		/// <returns>Objects that intersect with the specified ray.</returns>
		public void GetColliding(ref Ray checkRay, List<T> result, float maxDistance = float.PositiveInfinity)
		{
			float distance;
			// Is the input ray at least partially in this node?
			if (!bounds.IntersectRay(checkRay, out distance) || distance > maxDistance)
			{
				return;
			}

			// Check against any objects in this node
			for (var i = 0; i < objects.Count; i++)
			{
				if (objects[i].Bounds.IntersectRay(checkRay, out distance) && distance <= maxDistance)
				{
					result.Add(objects[i].Obj);
				}
			}

			// Check children
			if (children != null)
			{
				for (var i = 0; i < 8; i++)
				{
					children[i].GetColliding(ref checkRay, result, maxDistance);
				}
			}
		}

		public void GetWithinFrustum(Plane[] planes, List<T> result)
		{
			// Is the input node inside the frustum?
			if (!GeometryUtility.TestPlanesAABB(planes, bounds))
			{
				return;
			}

			// Check against any objects in this node
			for (var i = 0; i < objects.Count; i++)
			{
				if (GeometryUtility.TestPlanesAABB(planes, objects[i].Bounds))
				{
					result.Add(objects[i].Obj);
				}
			}

			// Check children
			if (children != null)
			{
				for (var i = 0; i < 8; i++)
				{
					children[i].GetWithinFrustum(planes, result);
				}
			}
		}

		/// <summary>
		/// Set the 8 children of this octree.
		/// </summary>
		/// <param name="childOctrees">The 8 new child nodes.</param>
		public void SetChildren(BoundsOctreeNode<T>[] childOctrees)
		{
			if (childOctrees.Length != 8)
			{
				Debug.LogError("Child octree array must be length 8. Was length: " + childOctrees.Length);
				return;
			}

			children = childOctrees;
		}

		public Bounds GetBounds()
		{
			return bounds;
		}

		/// <summary>
		/// Draws node boundaries visually for debugging.
		/// Must be called from OnDrawGizmos externally. See also: DrawAllObjects.
		/// </summary>
		/// <param name="depth">Used for recurcive calls to this method.</param>
		public void DrawAllBounds(float depth = 0)
		{
			var tintVal = depth / 7; // Will eventually get values > 1. Color rounds to 1 automatically
			var color = new Color(tintVal, 0, 1.0f - tintVal);

			var thisBounds = new Bounds(Center, new Vector3(adjLength, adjLength, adjLength));

			if (children != null)
			{
				depth++;
				for (var i = 0; i < 8; i++)
				{
					children[i].DrawAllBounds(depth);
				}
			}

			Gizmos.color = Color.white;
		}

		/// <summary>
		/// Draws the bounds of all objects in the tree visually for debugging.
		/// Must be called from OnDrawGizmos externally. See also: DrawAllBounds.
		/// </summary>
		public void DrawAllObjects()
		{
			var tintVal = BaseLength / 20;
			Gizmos.color = new Color(0, 1.0f - tintVal, tintVal, 0.25f);

			foreach (var obj in objects)
			{
				Gizmos.DrawCube(obj.Bounds.center, obj.Bounds.size);
			}

			if (children != null)
			{
				for (var i = 0; i < 8; i++)
				{
					children[i].DrawAllObjects();
				}
			}

			Gizmos.color = Color.white;
		}

		/// <summary>
		/// We can shrink the octree if:
		/// - This node is >= double minLength in length
		/// - All objects in the root node are within one octant
		/// - This node doesn't have children, or does but 7/8 children are empty
		/// We can also shrink it if there are no objects left at all!
		/// </summary>
		/// <param name="minLength">Minimum dimensions of a node in this octree.</param>
		/// <returns>The new root, or the existing one if we didn't shrink.</returns>
		public BoundsOctreeNode<T> ShrinkIfPossible(float minLength)
		{
			if (BaseLength < 2 * minLength)
			{
				return this;
			}

			if (objects.Count == 0 && (children == null || children.Length == 0))
			{
				return this;
			}

			// Check objects in root
			var bestFit = -1;
			for (var i = 0; i < objects.Count; i++)
			{
				var curObj = objects[i];
				var newBestFit = BestFitChild(curObj.Bounds.center);
				if (i == 0 || newBestFit == bestFit)
				{
					// In same octant as the other(s). Does it fit completely inside that octant?
					if (Encapsulates(childBounds[newBestFit], curObj.Bounds))
					{
						if (bestFit < 0)
						{
							bestFit = newBestFit;
						}
					}
					else
					{
						// Nope, so we can't reduce. Otherwise we continue
						return this;
					}
				}
				else
				{
					return this; // Can't reduce - objects fit in different octants
				}
			}

			// Check objects in children if there are any
			if (children != null)
			{
				var childHadContent = false;
				for (var i = 0; i < children.Length; i++)
				{
					if (children[i].HasAnyObjects())
					{
						if (childHadContent)
						{
							return this; // Can't shrink - another child had content already
						}

						if (bestFit >= 0 && bestFit != i)
						{
							return this; // Can't reduce - objects in root are in a different octant to objects in child
						}

						childHadContent = true;
						bestFit = i;
					}
				}
			}

			// Can reduce
			if (children == null)
			{
				// We don't have any children, so just shrink this node to the new size
				// We already know that everything will still fit in it
				SetValues(BaseLength / 2, minSize, looseness, childBounds[bestFit].center);
				return this;
			}

			// No objects in entire octree
			if (bestFit == -1)
			{
				return this;
			}

			// We have children. Use the appropriate child as the new root node
			return children[bestFit];
		}

		/// <summary>
		/// Find which child node this object would be most likely to fit in.
		/// </summary>
		/// <param name="objBounds">The object's bounds.</param>
		/// <returns>One of the eight child octants.</returns>
		public int BestFitChild(Vector3 objBoundsCenter)
		{
			return (objBoundsCenter.x <= Center.x ? 0 : 1) + (objBoundsCenter.y >= Center.y ? 0 : 4) +
				(objBoundsCenter.z <= Center.z ? 0 : 2);
		}

		/// <summary>
		/// Checks if this node or anything below it has something in it.
		/// </summary>
		/// <returns>True if this node or any of its children, grandchildren etc have something in them</returns>
		public bool HasAnyObjects()
		{
			if (objects.Count > 0)
			{
				return true;
			}

			if (children != null)
			{
				for (var i = 0; i < 8; i++)
				{
					if (children[i].HasAnyObjects())
					{
						return true;
					}
				}
			}

			return false;
		}

		/*
		/// <summary>
		/// Get the total amount of objects in this node and all its children, grandchildren etc. Useful for debugging.
		/// </summary>
		/// <param name="startingNum">Used by recursive calls to add to the previous total.</param>
		/// <returns>Total objects in this node and its children, grandchildren etc.</returns>
		public int GetTotalObjects(int startingNum = 0) {
		    int totalObjects = startingNum + objects.Count;
		    if (children != null) {
		        for (int i = 0; i < 8; i++) {
		            totalObjects += children[i].GetTotalObjects();
		        }
		    }
		    return totalObjects;
		}
		*/

		// #### PRIVATE METHODS ####

		/// <summary>
		/// Set values for this node.
		/// </summary>
		/// <param name="baseLengthVal">Length of this node, not taking looseness into account.</param>
		/// <param name="minSizeVal">Minimum size of nodes in this octree.</param>
		/// <param name="loosenessVal">Multiplier for baseLengthVal to get the actual size.</param>
		/// <param name="centerVal">Centre position of this node.</param>
		private void SetValues(
			float baseLengthVal, float minSizeVal, float loosenessVal, Vector3 centerVal,
			IEqualityComparer<T> comparer = null)
		{
			BaseLength = baseLengthVal;
			minSize = minSizeVal;
			looseness = loosenessVal;
			Center = centerVal;
			adjLength = looseness * baseLengthVal;

			// Create the bounding box.
			var size = new Vector3(adjLength, adjLength, adjLength);
			bounds = new Bounds(Center, size);

			var quarter = BaseLength / 4f;
			var childActualLength = BaseLength / 2 * looseness;
			var childActualSize = new Vector3(childActualLength, childActualLength, childActualLength);
			childBounds = new Bounds[8];
			childBounds[0] = new Bounds(Center + new Vector3(-quarter, quarter, -quarter), childActualSize);
			childBounds[1] = new Bounds(Center + new Vector3(quarter, quarter, -quarter), childActualSize);
			childBounds[2] = new Bounds(Center + new Vector3(-quarter, quarter, quarter), childActualSize);
			childBounds[3] = new Bounds(Center + new Vector3(quarter, quarter, quarter), childActualSize);
			childBounds[4] = new Bounds(Center + new Vector3(-quarter, -quarter, -quarter), childActualSize);
			childBounds[5] = new Bounds(Center + new Vector3(quarter, -quarter, -quarter), childActualSize);
			childBounds[6] = new Bounds(Center + new Vector3(-quarter, -quarter, quarter), childActualSize);
			childBounds[7] = new Bounds(Center + new Vector3(quarter, -quarter, quarter), childActualSize);

			comparator = comparer ?? EqualityComparer<T>.Default;
		}

		/// <summary>
		/// Private counterpart to the public Add method.
		/// </summary>
		/// <param name="obj">Object to add.</param>
		/// <param name="objBounds">3D bounding box around the object.</param>
		private void SubAdd(T obj, Bounds objBounds)
		{
			// We know it fits at this level if we've got this far

			// We always put things in the deepest possible child
			// So we can skip some checks if there are children aleady
			if (!HasChildren)
			{
				// Just add if few objects are here, or children would be below min size
				if (objects.Count < NUM_OBJECTS_ALLOWED || BaseLength / 2 < minSize)
				{
					var newObj = new OctreeObject { Obj = obj, Bounds = objBounds };
					objects.Add(newObj);
					return; // We're done. No children yet
				}

				// Fits at this level, but we can go deeper. Would it fit there?
				// Create the 8 children
				int bestFitChild;
				if (children == null)
				{
					Split();
					if (children == null)
					{
						Debug.LogError("Child creation failed for an unknown reason. Early exit.");
						return;
					}

					// Now that we have the new children, see if this node's existing objects would fit there
					for (var i = objects.Count - 1; i >= 0; i--)
					{
						var existingObj = objects[i];
						// Find which child the object is closest to based on where the
						// object's center is located in relation to the octree's center
						bestFitChild = BestFitChild(existingObj.Bounds.center);
						// Does it fit?
						if (Encapsulates(children[bestFitChild].bounds, existingObj.Bounds))
						{
							children[bestFitChild]
								.SubAdd(existingObj.Obj, existingObj.Bounds); // Go a level deeper					
							objects.Remove(existingObj);                      // Remove from here
						}
					}
				}
			}

			// Handle the new object we're adding now
			var bestFit = BestFitChild(objBounds.center);
			if (Encapsulates(children[bestFit].bounds, objBounds))
			{
				children[bestFit].SubAdd(obj, objBounds);
			}
			else
			{
				// Didn't fit in a child. We'll have to it to this node instead
				var newObj = new OctreeObject { Obj = obj, Bounds = objBounds };
				objects.Add(newObj);
			}
		}

		/// <summary>
		/// Private counterpart to the public <see cref="Remove(T, Bounds)" /> method.
		/// </summary>
		/// <param name="obj">Object to remove.</param>
		/// <param name="objBounds">3D bounding box around the object.</param>
		/// <returns>True if the object was removed successfully.</returns>
		private bool SubRemove(T obj, Bounds objBounds, bool parentContained)
		{
			var removed = false;

			for (var i = 0; i < objects.Count; i++)
			{
				var octreeObject = objects[i];
				if (comparator.Equals(octreeObject.Obj, obj))
				{
					objects.RemoveBySwap(i);
					removed = true;

					break;
				}
			}

			if (!removed && children != null)
			{
				var bestFitIndex = BestFitChild(objBounds.center);
				BoundsOctreeNode<T> child = children[bestFitIndex];

				if (objBounds.Intersects(child.bounds))
				{
					removed = child.SubRemove(obj, objBounds, parentContained);
				}

				if (!removed)
				{
					for (var i = 0; i < children.Length; i++)
					{
						if (bestFitIndex == i) { continue; }

						child = children[i];

						var contained = parentContained;
						if (!contained)
						{
							if (Encapsulates(objBounds, child.bounds))
							{
								contained = true;
							}
							else if (!objBounds.Intersects(child.bounds))
							{
								continue;
							}
						}

						if (!objBounds.Intersects(child.bounds)) { continue; }

						removed = child.SubRemove(obj, objBounds, contained);
						if (removed) { break; }
					}
				}
			}

			if (removed && children != null)
			{
				// Check if we should merge nodes now that we've removed an item
				if (ShouldMerge())
				{
					Merge();
				}
			}

			return removed;
		}

		private int SubRemoveAll(Bounds objBounds)
		{
			var removedCount = objects.RemoveAll(obj => obj.Bounds.Intersects(objBounds));

			if (removedCount < 1 && children != null)
			{
				var bestFitChild = BestFitChild(objBounds.center);
				removedCount = children[bestFitChild].SubRemoveAll(objBounds);
			}

			if (removedCount > 0 && children != null)
			{
				// Check if we should merge nodes now that we've removed an item
				if (ShouldMerge())
				{
					Merge();
				}
			}

			return removedCount;
		}

		/// <summary>
		/// Splits the octree into eight children.
		/// </summary>
		private void Split()
		{
			var quarter = BaseLength / 4f;
			var newLength = BaseLength / 2;
			children = new BoundsOctreeNode<T>[8];
			children[0] = new BoundsOctreeNode<T>(newLength, minSize, looseness,
				Center + new Vector3(-quarter, quarter, -quarter));
			children[1] = new BoundsOctreeNode<T>(newLength, minSize, looseness,
				Center + new Vector3(quarter, quarter, -quarter));
			children[2] = new BoundsOctreeNode<T>(newLength, minSize, looseness,
				Center + new Vector3(-quarter, quarter, quarter));
			children[3] = new BoundsOctreeNode<T>(newLength, minSize, looseness,
				Center + new Vector3(quarter, quarter, quarter));
			children[4] = new BoundsOctreeNode<T>(newLength, minSize, looseness,
				Center + new Vector3(-quarter, -quarter, -quarter));
			children[5] = new BoundsOctreeNode<T>(newLength, minSize, looseness,
				Center + new Vector3(quarter, -quarter, -quarter));
			children[6] = new BoundsOctreeNode<T>(newLength, minSize, looseness,
				Center + new Vector3(-quarter, -quarter, quarter));
			children[7] = new BoundsOctreeNode<T>(newLength, minSize, looseness,
				Center + new Vector3(quarter, -quarter, quarter));
		}

		/// <summary>
		/// Merge all children into this node - the opposite of Split.
		/// Note: We only have to check one level down since a merge will never happen if the children already have children,
		/// since THAT won't happen unless there are already too many objects to merge.
		/// </summary>
		private void Merge()
		{
			// Note: We know children != null or we wouldn't be merging
			for (var i = 0; i < 8; i++)
			{
				BoundsOctreeNode<T> curChild = children[i];
				var numObjects = curChild.objects.Count;
				for (var j = numObjects - 1; j >= 0; j--)
				{
					var curObj = curChild.objects[j];
					objects.Add(curObj);
				}
			}

			// Remove the child nodes (and the objects in them - they've been added elsewhere now)
			children = null;
		}

		/// <summary>
		/// Checks if outerBounds encapsulates innerBounds.
		/// </summary>
		/// <param name="outerBounds">Outer bounds.</param>
		/// <param name="innerBounds">Inner bounds.</param>
		/// <returns>True if innerBounds is fully encapsulated by outerBounds.</returns>
		private static bool Encapsulates(Bounds outerBounds, Bounds innerBounds)
		{
			return outerBounds.Contains(innerBounds.min) && outerBounds.Contains(innerBounds.max);
		}

		/// <summary>
		/// Checks if there are few enough objects in this node and its children that the children should all be merged into this.
		/// </summary>
		/// <returns>True there are less or the same abount of objects in this and its children than numObjectsAllowed.</returns>
		private bool ShouldMerge()
		{
			var totalObjects = objects.Count;
			if (children != null)
			{
				foreach (BoundsOctreeNode<T> child in children)
				{
					if (child.children != null)
					{
						// If any of the *children* have children, there are definitely too many to merge,
						// or the child woudl have been merged already
						return false;
					}

					totalObjects += child.objects.Count;
				}
			}

			return totalObjects <= NUM_OBJECTS_ALLOWED;
		}
	}
}
