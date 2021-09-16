# ValheimPerformanceOptimizations [H&H Compatible]

Rendering, logic, and loading time optimizations for both client and server versions of Valheim.

You can use the mod on either the server or the client, or both, it should work regardless.

## New in 0.7.0

* Optimizations for structural integrity
* New object pooling system for build pieces - less lag when entering bases
* New smoke rendering solution - less lag rendering smoke puffs
* Snow storms no longer tank fps
* Fixed grass not appearing in the main menu
* Fixed objects spawning at 0 0

Rest of the changes can be found in `CHANGELOG.md`

## Features

* New object pooling system for build pieces - less lag when entering bases
* New smoke rendering solution - less lag rendering smoke puffs
* Snow storms no longer tank fps
* Various optimizations for GPU rendering
* Multithreaded terrain loading
* Much faster loading times
* Various improvements to AI
* Optimizations for structural integrity

## Stats

* 5-10 ms faster GPU render times in bases (3 fps without the mod -> 15 fps with)
* General game stability improvements - less stutters in bases
* 20+ seconds faster world loading times, especially for big worlds (excluding the first launch)
* Less stutters when loading new terrain  
* General game logic performance improvements (no concrete data on framerates)

## Configuration

The mod config is stored in the `dev.ontrigger.vpo.cfg` file.

Most optimizations done by the mod do not affect the gameplay in any way, 
however some of its optimizations might cause compatibility issues with other mods.

* Threaded terrain collision baking

  If enabled terrain is generated in parallel, this reduces lag spikes when moving through the world. If you see terrain disappear, please report it on github, disabling this option will likely fix the issue.

* Object pooling

  If enabled vegetation objects are taken from a pool, instead of creating and destroying them everytime. This greatly increases performance when generating new terrain. If you notice some objects becoming invisible, please report it on github, disabling this option will likely fix the issue.

  * Object pooling multiplier - this option does not do anything useful for now

## Manually compiling the mod

In order to manually compile the source code of the mod, 
create a file called `Environment.props` inside the project base and change the Valheim install path to your location.

```
<?xml version="1.0" encoding="utf-8"?>
<Project ToolsVersion="Current" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup>
    <!-- Needs to be your path to the base Valheim folder -->
    <VALHEIM_INSTALL>E:\Steam\steamapps\common\Valheim</VALHEIM_INSTALL>
  </PropertyGroup>
</Project>
```

## Contributors

* ontrigger
* MSchmoecker
