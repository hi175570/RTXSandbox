# RTX Sandbox modding

RTX Sandbox's desktop launcher hosts BepInEx 6 CoreCLR. It runs the official
`AssemblyPatcher` before loading the game, then the game starts the chainloader
once. No Unity Doorstop injection files or `winhttp.dll` are needed. At startup,
BepInEx discovers standard `BasePlugin` DLLs under:

```text
<game directory>/BepInEx/plugins/
```

The framework assemblies, HarmonyX, Mono.Cecil and MonoMod runtime chain are
kept under `<game directory>/BepInEx/core/`. `Managed/` contains the game and
its rendering/runtime dependencies; BepInEx framework files are not duplicated
there.

## Desktop startup patchers

Launch the root `RTXSandbox.exe` (Windows) / `RTXSandbox` (Linux/macOS), or run
`dotnet Managed/RTXSandbox.Launcher.dll`. `dotnet run --project RTXSandbox.csproj`
uses the root launcher too. Build and managed platform publish include the launcher.

The startup order is **launcher → BepInEx AssemblyPatcher → patched game assembly
→ BepInEx NetChainloader → game window**. Place BepInEx 6 `BasePatcher` assemblies
under `BepInEx/patchers/`. `[PatcherPluginInfo]`, `[TargetAssembly]`, `[TargetType]`,
`Initialize`, and `Finalizer` are handled by BepInEx's own patching engine.
Targets are managed game DLLs in `Managed/` that have not already been loaded.
The launcher, framework and runtime assemblies already executing are excluded.
Patchers must use Cecil metadata and avoid loading game types before patching.

Patches are applied in memory, leaving the original game DLLs intact. The launcher
has its own dependency manifest: putting the unpatched game in the launcher's
trusted-platform assembly list would make CoreCLR ignore patched bytes. Game
dependencies and native libraries are resolved using the game's dependency manifest.

Preloader messages go to `BepInEx/preloader.log`; startup failures also write
`bepinex_preloader_error.log` beside the executable. A patcher error stops startup
with exit code 1 rather than running a partially patched game. Set
`RTX_SANDBOX_DISABLE_BEPINEX=1` to start without both patchers and ordinary plugins.
`--mod-diagnostics` reports preloader status, patch counts and plugin errors.
Direct `dotnet Managed/RTXSandbox.dll` launch is rejected when patchers are present.
Self-tests, physics probes, performance benchmarks and non-Mod GPU smoke tests
skip patchers so installed mods cannot contaminate their results.

The desktop preloader is separate from the existing Android plugin adapter.
Android startup patchers are not implemented by this change. Unity-specific
plugins and BepInEx 5 patcher APIs are not made compatible by this launcher.

Run `pwsh -File scripts/Test-Preloader.ps1` after a Release build. The test uses
temporary packages, adds real game metadata with a patcher, verifies it from an
ordinary plugin, checks repeat launch, failure/recovery and direct-launch guards,
checks that the disk DLL is unchanged, then removes only its own temporary files.
The fixtures under `Examples/PreloaderProbe` are tests; they are not shipped or
installed into the player's mod folders. CI runs this test on all three desktop OSes.

The game currently uses BepInEx `6.0.0-be.785` on a Visual Studio-compatible .NET 9 host by default. An optional .NET 10 host can be built with `-p:RTXSandboxHostTargetFramework=net10.0`. HarmonyX patching, plugin dependency ordering, logging and per-plugin `.cfg` files are included with the game. The in-game Mods page lists loaded plugins and dependency failures, opens both plugin folders, and can edit/save/reload common configuration values.

## In-game manual workspace

The Mods page also contains a player-driven file workspace. It never watches or automatically compiles files. Right-click a file to edit text, compile, move, or delete it; delete always asks for confirmation and moves the file to `BepInEx/trash` so it remains recoverable.

Any file detected as UTF text (up to 4 MiB) can be treated as one C# source unit, regardless of whether its extension is `.cs`, `.txt`, or something else. **Compile** targets `net6.0` and references:

- the .NET 6 SDK reference assemblies;
- every compatible managed assembly in the game's `Managed` directory;
- the BepInEx, HarmonyX, Mono.Cecil, and MonoMod APIs in `BepInEx/core`;
- compatible managed DLLs recursively discovered under `BepInEx/plugins`, including prerequisite mods.

Native DLLs and assemblies targeting a runtime newer than .NET 6 are not valid compiler references and are skipped. Compilation requires a locally installed .NET SDK capable of building `net6.0`; having only the runtime is not enough.

If the selected source is loose in the root of `plugins`, explicit compilation first creates a same-name folder and moves the source plus matching DLL/PDB/deps files into it. Sources already inside a folder stay there. The source is never deleted by compilation. Build scratch files use the system temporary directory, and the resulting DLL is placed beside the source. Restart the game to load a newly compiled or replaced DLL.

The built-in editor is deliberately plain: type, save, discard unsaved changes, or roll back to the copy retained before the latest save. Closing it never saves implicitly.

## Empty by default

The game ships with an empty `BepInEx/plugins` directory. Built-in blocks and data files are base-game content and never appear in the Mods screen. Add only plugins that the player explicitly installs.

```powershell
dotnet build RTXSandbox.sln -c Release
dotnet run -c Release -- --mod-diagnostics
dotnet run -c Release
```

The diagnostic command loads plugins without opening the game window. A successful run reports `BepInEx started: True`.

## Minimal plugin

Create a class library targeting `netstandard2.1` or `net6.0` when its APIs are sufficient; `net7.0`, `net8.0`, and `net9.0` plugins can also be loaded by the default .NET 9 host when their own dependencies are compatible. A plugin that specifically targets `net10.0` requires the optional net10 Host. Reference `RTXSandbox.ModAPI` and `BepInEx.NET.CoreCLR` from `https://nuget.bepinex.dev/v3/index.json`.

```csharp
using System.Numerics;
using BepInEx;
using BepInEx.NET.Common;
using RTXSandbox.ModAPI;

[BepInPlugin("author.example", "Example Mod", "1.0.0")]
public sealed class Plugin : BasePlugin
{
    public override void Load()
    {
        SandboxApi.WhenReady(context =>
        {
            ushort id = context.Blocks.Register(new BlockDefinition(
                "author:blue_lamp",
                "Blue Lamp",
                new Vector3(0.1f, 0.2f, 0.8f),
                roughness: 0.2f,
                emission: new Vector3(0.2f, 0.8f, 6f)),
                force: true);

            context.Events.WorldReady += world =>
                world.SetBlock(10, 12, 10, id);

            context.Tools.Register(new BuildToolDefinition(
                "author:blue_painter",
                "Blue Painter",
                use => use.World.SetBlock(
                    use.X + use.NormalX,
                    use.Y + use.NormalY,
                    use.Z + use.NormalZ,
                    id),
                description: "Places a blue lamp on the targeted face."),
                force: true);
        });
    }
}
```

## Override rules

The registry intentionally permits Minecraft-style hard overrides:

```csharp
context.Blocks.Register(new BlockDefinition(
    "rtxsandbox:stone",
    "My Stone",
    new Vector3(0.1f, 0.2f, 0.8f),
    roughness: 0.08f,
    metallic: 0.9f,
    id: 3),
    force: true);
```

With `force: true`, an existing namespaced key or numeric ID is removed and the latest registration wins. If the same key is replaced with `id: 0`, its existing runtime ID is retained. Without `force`, collisions throw so accidental conflicts are visible. `Remove(key)` unregisters a definition. Block properties are mutable; after an in-place edit, call `NotifyChanged()` so rendering/material consumers refresh.

This is deliberately not a security boundary. Load order can change the winning override, and replacing reserved semantics such as air or water can break worlds or gameplay.

## Public API

- `IBlockRegistry.Register`: registers or force-overrides materials in a 4,096-slot renderer table (IDs 1–4094; 4095 is the unknown-material sentinel).
- `BlockDefinition`: albedo, roughness, metallic response, emission, transmission/IOR, collision, fluid, replacement, category, tags, procedural pattern, 16×16 `TextureGenerator` / `TextureVariation` / `TextureSeed`, sound/particle metadata and palette behavior.
- `BlockDefinition.AffectedByGravity`: opts a material into `Simple` voxel physics.
- `IBuildToolRegistry`: registers editor tools; `force: true` uses the same last-writer-wins behavior as blocks.
- `BuildToolUseContext`: target, face normal, current material, brush radius, alternate input and direct `IVoxelWorld` access.
- `IItemRegistry`: compatibility/general-purpose registry for mods that want stackable items.
- `IInventory`: slot access, stack insertion/removal, counts and selected hotbar slot.
- `IRecipeRegistry` / `ItemDefinition`: legacy-compatible optional gameplay building blocks; the creative base game does not load a tech progression.
- `IVoxelWorld`: bounded read/write access plus seed, generation preset and biome metadata.
- `IMicroVoxelService`: optional precision API from `context.GetService<IMicroVoxelService>()`; exposes grid selection, sparse cell queries, exact-grid painting, and Undo/Redo. `force: true` permits protected/direct replacement for trusted mods.
- `IRenderBackendService`: lists every renderer and whether it is actually available, switches the active backend by ID, and resets progressive history without referencing the game assembly.
- `IVoxelPhysicsService`: reports the BEPU structural-grid backend, active Grid／substep counts and requests a new structural evaluation. Six-face structural fracture is an always-on world rule, not a user or Mod mode switch.
- `WorldReady`: runs after registered blocks and a saved world have been loaded.
- `Updating`: per-frame game-thread callback.
- `VoxelChanged`: reports the position and old/new block IDs.
- `IModLogger`: game-owned logging that remains available without depending on BepInEx internals.
- `IModContext.Game`: the actual `SandboxGame` instance as `object`, for unrestricted access when desired.
- `IModContext.Services` / `GetService<T>`: retrieves stable services such as `IRenderBackendService`, `IVoxelPhysicsService` and `IMicroVoxelService`, or concrete game systems including `BuildToolSystem`, `BlockRegistry`, `VoxelWorld`, `PlayerController`, `Camera`, `WorldClock`, `RenderBackendHost`, `WorldSaveService`, `GameSettings` and the game window.
- `SandboxApi.RequireService<T>`: concise typed service lookup when the service must exist.

When a registered build tool runs through the editor, direct voxel changes emitted during its callback are coalesced into one Undo/Redo command. A mod can intentionally bypass editor history by writing to `IVoxelWorld` from another callback.

The priority event bus includes legacy block/item/player events plus `PlayerMoveEvent`, world ticks and before/after render events. Cancellable events can change values or stop the base action. Legacy survival-oriented contracts remain binary compatibility helpers, not the base game's active loop.

Each mod event handler is isolated. If one handler throws, the error is logged and remaining mods continue.

Initializers passed to `WhenReady` run early enough to register blocks before a save is loaded. Player, renderer and clock services are guaranteed by the `WorldReady` event. For typed access to concrete systems, reference the game's `RTXSandbox.dll` in addition to the stable Mod API:

```csharp
context.Events.WorldReady += _ =>
{
    var game = context.GetService<RTXSandbox.SandboxGame>()!;
    game.Clock.SetTime(18f);
    game.Renderer.ResetAccumulation();
};
```

`WorldReady` is published again after the editor creates a preset or reloads a save. Structure/city mods can therefore place content without being part of the base generator. Mods referencing the concrete game assembly may inspect `VoxelWorld.GenerationSettings` or call `Regenerate(new WorldGenerationSettings { ... })`; a mod should ask the player before replacing an edited world.

For method-level replacement, every CoreCLR `BasePlugin` already receives a `HarmonyInstance`. Mods can call `HarmonyInstance.PatchAll(...)` and use ordinary Harmony prefix/postfix/transpiler patches, including against private members. That escape hatch is intentionally unrestricted; patch conflicts are resolved by Harmony priorities and plugin load order rather than by the public API.

## Save compatibility

Runtime block IDs are not stable identifiers. World saves store namespaced keys such as `author:blue_lamp`, then remap them to the IDs assigned in the current session. If a mod is removed, its blocks safely become air when that world is loaded.

Voxel storage and public block IDs use `ushort`. Save format 6 stores 16-bit voxels plus sparse micro-voxel boxes; formats 1–4 are widened automatically and format 5 remains loadable. Both normal and micro materials are remapped by namespaced key. Mods compiled against the earlier byte-ID API must be rebuilt against the current `RTXSandbox.ModAPI`.

Use a globally unique lowercase key prefix and never rename a released block key without providing a migration strategy.

## Safety

BepInEx plugins are arbitrary .NET code with the same filesystem and network permissions as the game. Only install mods from authors you trust. Restart the game after adding, removing or updating DLLs.
