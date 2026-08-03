# ValheimPerformanceOptimizations

Rendering, logic, and loading time optimizations for both client and server versions of Valheim.

Unlike other mods of its kind, VPO does not disable, modify, throttle or otherwise change vanilla behavior at all.
Please note that I have not tested this mod on servers outside

You can use the mod on either the server or the client, or both, it should work regardless.

## Changes in 1.0.0

Major rewrite for current Valheim:

* Rewritten ZNetScene object streaming - no per-frame overhead when creating/destroying world objects when not needed
* Burst-accelerated water wave jobs - cheaper wave math for floaters, fish, and surface queries
* Burst terrain vertex color generation with fewer Color[] allocations
* Modernized threaded terrain collision baking - less hitching while exploring
* ZSFX optimizations for audiosources that keep playing while outside audible range
* Light flicker culling - skip expensive flicker updates for point lights outside the camera
* WearNTear support caching - slightly lower structural integrity CPU cost in large bases
* Time-sliced reflection renderer - render cubemap faces over multiple frames
* Prefab/particle cleanup - fire particles no longer render outside view frustum
* Faster server-side ZDO ownership release scans
* VisEquipment ZDO int caching and BinarySearchDictionary allocation fix

Rest of the changes can be found in `CHANGELOG.md`

## Features

* Rewritten world object streaming for less overhead when moving through the world
* Burst-accelerated water and terrain generation
* Threaded terrain collision baking - less hitching when loading new terrain
* Structural integrity caching - better performance in large bases
* Audiosource culling for distant looping sounds
* Culled light flicker updates for off-screen lights
* Time-sliced reflection probes
* Particle culling / GPU instancing improvements
* Faster server ownership handoff scans
* Optional physics step cap for denser bases

## Configuration

The mod config is stored in the `dev.ontrigger.vpo.cfg` file.

Most optimizations done by the mod do not affect the gameplay in any way,
however some of its optimizations might cause compatibility issues with other mods.

* Threaded terrain collision baking

  Experimental: if enabled, terrain collision is generated in parallel, which reduces lag spikes when moving through the world. If you see terrain disappear, please report it on GitHub; disabling this option will likely fix the issue.

* Max physics updates per frame

  The engine can run physics many times per frame, which is often the most expensive part of Valheim. Lowering this (default 8, range 5–15) can significantly boost FPS in bases at the cost of less accurate physics.

## Contributors

* ontrigger
* MSchmoecker
