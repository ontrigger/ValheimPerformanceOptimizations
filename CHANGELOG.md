# Changes in 0.7.5

* Jotunn is no longer required to use the mod
* Fixed more EffectArea errors

# Changes in 0.7.4

* Fixed crazy spawnrates
* Fixed being unable to breed animals
* ValheimRaft compatibility (at the cost of performance)
* Fix errors after logging out/quitting

# Changes in 0.7.3

* Another attempt at fixing the roof checks

# Changes in 0.7.2

* Fixed the 'Collection was modified' error
* Potential fix for leaking roofs

# Changes in 0.7.1

* Fixed snow storm particles appearing inside buildings
* Fixed being unable to sleep
* Fixed other related AOE effects not working

# Changes in 0.7

* Optimizations for structural integrity
* New object pooling system for build pieces - less lag when entering bases
* New smoke rendering solution - less lag rendering smoke puffs
* Snow storms no longer tank fps
* Fixed grass not appearing in the main menu
* Fixed objects spawning at 0 0

# Changes in 0.6.1

* Fixed an incompatibility with BetterWards causing mobs inside the ward radius to flee
* Fixed the GPU being invoked on the server causing an exception at startup

# Changes in 0.6.0

* Optimized rendering of build pieces with straw materials
* Rewrote the threaded terrain collision baking to use all cores (enable it in the config)
* Fixed incompatibility with ValheimRAFT
* Fixed crash when only terrain collision baking was enabled

# Changes in 0.5.2

* Fixed certain objects respawning upon relog

# Changes in 0.5.1

* Fixed certain objects disappearing

# Changes in 0.5.0

* Object pooling makes loading vegetation in new and old chunks much smoother

# Changes in 0.4.2

* Fixed incompatibility with EpicLoot

# Changes in 0.4.1

* Fixed optimized terrain disappearing upon relogging 

# Changes in 0.4.0

* Threaded terrain collision generation
* Threaded world loading
* Smoke rendering performance improvements
* Small memory allocation and performance improvements for chunk generation

# Changes in 0.3.1

* Fixed multiple crashes caused by logging out and back in into any world
* Tiny performance improvement for comfort level calculation

# Changes in 0.3.0

* First public release
