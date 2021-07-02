# ValheimPerformanceOptimizations

Rendering, logic, and loading time optimizations for both client and server versions of Valheim.

## Changes in 0.4.1

* Fixed optimized terrain disappearing upon relogging

## New in 0.4.0

* Threaded terrain collision generation decreases stutters when loading chunks
* Smoke rendering performance improvements

## Stats

* 5-10 ms faster GPU render times in bases (3 fps without the mod -> 15 fps with)
* General game stability improvements - less stutters in bases
* 20+ seconds faster world loading times, especially for big worlds (excluding the first launch)
* General game logic performance improvements (no concrete data on framerates)

## Development Setup

Create a file called `Environment.props` inside the project base and change the Valheim install path to your location.

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
