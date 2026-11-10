# RTX Sandbox modding

RTX Sandbox deliberately uses **BepInEx 6 CoreCLR + HarmonyX as an unrestricted
game-hook framework**. There is no separate `RTXSandbox.ModAPI` assembly and no
service-locator compatibility layer. A plugin references the current
`Managed/RTXSandbox.dll`, patches or calls the game directly, and therefore must
be rebuilt when an incompatible game update changes internal types.

## Desktop loading

The root launcher runs BepInEx `AssemblyPatcher` before loading the game, then
starts the CoreCLR chainloader exactly once:

```text
RTXSandbox.exe
├─ BepInEx/patchers/       startup Cecil patchers
├─ BepInEx/plugins/        BasePlugin DLLs and their dependencies
├─ BepInEx/core/           BepInEx, HarmonyX, Cecil and MonoMod
└─ Managed/RTXSandbox.dll game assembly referenced by plugins
```

Unity Doorstop and `winhttp.dll` are not used because this is not a Unity game.
Patchers operate on the in-memory assembly; they do not rewrite the DLL on disk.
A patcher failure stops startup instead of launching a partly patched game.
Set `RTX_SANDBOX_DISABLE_BEPINEX=1` for recovery and use
`--mod-diagnostics` to inspect the loader.

## Minimal direct-hook plugin

Target the same framework as the installed game (currently `net9.0`) and
reference `Managed/RTXSandbox.dll`, `BepInEx.NET.CoreCLR.dll` and `0Harmony.dll`.

```csharp
using BepInEx;
using BepInEx.NET.Common;
using HarmonyLib;
using RTXSandbox;

[BepInPlugin("author.example", "Direct Hook Example", "1.0.0")]
public sealed class Plugin : BasePlugin
{
    public override void Load()
    {
        HarmonyInstance.Patch(
            AccessTools.Method(typeof(SandboxGame), "OnLoad"),
            postfix: new HarmonyMethod(typeof(Plugin), nameof(GameLoaded)));
    }

    private static void GameLoaded(SandboxGame game)
    {
        // Direct game access. Reflection/Harmony can also reach private state.
        Log.LogInfo($"Loaded {game.Blocks.Count} game blocks");
    }
}
```

`Examples/ExampleMod` is the buildable reference. It registers a block by
patching `SandboxGame.OnLoad`; `Examples/SoftBodyPhysicsMod` is an impact-only
plastic-deformation example that patches the private energy-hit path. Continuous
soft-body simulation and glass fracture belong to the core game. Neither plugin
goes through a public Mod API.

## In-game workspace

The Mods page does not watch or compile files without a click. Its context menu
can edit, save, roll back, rename, move, compile or delete text files. Compile:

- preserves the original source;
- targets the running host major version (`net9.0` in the default build);
- references compatible assemblies under `Managed`, `BepInEx/core` and plugin
  folders, so prerequisite plugins can be referenced;
- puts a loose source file and its generated DLL/PDB/deps files into a dedicated
  plugin folder;
- uses a temporary build directory and never implements silent hot reload.

Delete is confirmed and moves content into `BepInEx/trash`. Restart the game
after replacing a plugin DLL.

## Android

Android has a managed BasePlugin adapter and loads player-provided assemblies
from `Documents/RTXSandbox/Mods`. It does not run the desktop preloader. Direct
game calls can work when the referenced types are linked into the Android host,
but Harmony RuntimeDetour, native dependencies, assembly isolation and every
desktop-private type are not guaranteed across Android ABIs. Build/install proof
does not count as proof that an arbitrary desktop hook works on a device.

## Compatibility and safety

- The desktop host is `net9.0` by default. An optional `net10.0` host can be
  built, but that does not make binary dependencies automatically compatible.
- Runtime block IDs are not stable. Persist namespaced block keys and provide
  migrations if a released key changes.
- Direct hooks have no safety boundary. Load order and Harmony priority decide
  conflicts; a plugin can access the same files, network and process state as
  the game.
- Put each mod and its private dependencies in its own subfolder. This reduces
  accidental collisions but is not full `AssemblyLoadContext` isolation.

## Validation

```powershell
dotnet build RTXSandbox.sln -c Release
dotnet run --project RTXSandbox.csproj -c Release --no-build -- --mod-diagnostics
pwsh -File scripts/Test-Preloader.ps1
```

The preloader test creates only temporary fixtures, verifies patch order,
failure recovery and unchanged on-disk game DLL hashes, then removes its own
files. Normal self-tests and non-Mod GPU smoke disable BepInEx so installed
plugins cannot contaminate their result. `--gpu-smoke-mod` explicitly verifies
the direct-hook example instead.
