# ValheimPerformanceOptimizations
Rendering, logic, and loading time optimizations for Valheim.
## Stats
* 5-10 ms faster GPU render times in bases (3 fps without the mod -> 15 fps with)
* General game stability improvements - less stutters in bases
* Up to 20 second faster world loading times (excluding the first launch)
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
