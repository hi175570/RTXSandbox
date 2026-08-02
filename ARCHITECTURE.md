# RTX Sandbox engine architecture

## Product boundary

RTX Sandbox is a creative voxel construction, lighting and physics laboratory. The base game owns the complete creative loop: world creation, movement and collision, block placement/destruction, precision building, editing history, material library, rendering, saves, time, physics controls and user interfaces. Mods extend or forcibly replace these systems; they are not a substitute for the base loop.

The current world remains a bounded, deterministic prototype. Infinite chunk streaming, rigid debris, volumetric weather, camera paths and native DXR/Vulkan RT require their own data and scheduling layers and must not be represented by non-functional settings.

## Dependency direction

```text
                          BepInEx plugins
                                |
                                v
Game systems -------> RTXSandbox.ModAPI <------- ModHost / BepInEx adapter
     |
     +-- Rendering/RenderBackendHost
     |      +-- Compute Path Tracing
     |      +-- PBR Forward Raster
     |      +-- Classic Raster
     |
     +-- World/VoxelWorld + generation presets
     +-- Gameplay/building + physics + player collision
     +-- Persistence/versioned world saves
     +-- UI/Screens
```

`RTXSandbox.ModAPI` targets `netstandard2.1` and does not reference OpenTK, ImGui, BepInEx or the game executable. BepInEx is the bundled loader and patching adapter; gameplay code communicates with mods through `ModHost` and the public API.

## Rendering

`IRenderBackend` is the engine-facing contract. `RenderBackendHost` owns exactly one backend, forwards resize/render/history calls, and changes modes atomically: the replacement must compile shaders and allocate GPU resources before the old backend is disposed.

| Mode | State | Purpose |
|---|---|---|
| Compute Path Tracing | Available | Progressive GI, dielectric Fresnel reflections, transmission and micro-voxels on OpenGL compute |
| PBR Forward | Available | Real-time mesh rendering, procedural atlas, sunlight, approximate sky reflection and transparency |
| Classic | Available | Low-cost textured voxel rendering for editing and compatibility |
| Hardware Ray Tracing | Planned/locked | Future DXR or Vulkan RT acceleration structures and shaders |
| Deferred Lighting | Planned/locked | Future chunk meshes, GBuffer and lighting pass |

All selectable backends must render ordinary voxels, precision geometry, material previews and the selection indicator. A catalog test prevents an unfinished backend from becoming selectable.

## Mod API layout

```text
RTXSandbox.ModAPI
├── Blocks/BlockContracts.cs
├── World/WorldContracts.cs
├── Rendering/RenderContracts.cs
├── Physics/PhysicsContracts.cs
└── ModContracts.cs        (shared and legacy-compatible contracts)
```

Mods should prefer `IModContext.GetService<IRenderBackendService>()`, `IVoxelPhysicsService` and `IMicroVoxelService`. Referencing `RTXSandbox.dll` remains an intentional unrestricted escape hatch, and Harmony patches can still force private behavior changes.

## Next structural milestones

1. Introduce chunk ownership and dirty-region mesh jobs before claiming an infinite world.
2. Split biome, climate, cave, river and decoration passes behind deterministic generator interfaces.
3. Move fluid state out of block identity into a sparse cell layer, then add flow and optical surface data.
4. Split the remaining inventory, toolbox, world creation and settings code into `UI/Screens` controllers.
5. Create a loader adapter project so the executable can run with a null loader or BepInEx without gameplay referencing loader packages.
6. Add a render graph/resource lifetime layer before Deferred or native hardware RT.

This order preserves the working game while removing coupling at boundaries that future features actually need.
