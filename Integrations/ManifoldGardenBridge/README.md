# Manifold Garden bridge

This integration runs inside the local Windows IL2CPP build of Manifold Garden
and publishes a versioned camera snapshot to a named memory mapping. RTX Sandbox
returns its own process id, frame counter and UTC heartbeat through the same
mapping, so both processes can distinguish a live peer from stale data. It does not
use Steam, Steamworks, a network server, or modified game assets.

The validated loader combination for this game is BepInEx `6.0.0-pre.1` with
`Logging.UnityLogListening = false`. The old loader's default Unity log adapter
fails while resolving this game's stripped IL2CPP metadata, while current
bleeding-edge build 788 reaches its chainloader and then access-violates in its
`il2cpp_runtime_invoke` detour. Disabling the pre.1 log adapter avoids that
unneeded path and allows the game and plugin chainloader to start normally.

## Install and test

Close Manifold Garden, then run from PowerShell 7:

```powershell
./Install-BepInEx.ps1
./Install-Plugin.ps1
```

Start `ManifoldGarden.exe` directly. In another terminal:

```powershell
dotnet run --project ./Probe/RTXSandbox.ManifoldGarden.Probe.csproj -c Release -- --samples 8 --interval 250
```

The probe succeeds only when it can open
`Local\RTXSandbox.ManifoldGarden.v1` and observe an advancing sequence. Camera
position, rotation, field of view, viewport size, process id, frame number and
UTC heartbeat are included. The production RTX Sandbox client displays the
connected Manifold Garden PID, frame and link age in its F3 HUD. The plugin's
`Performance.TargetFrameRate` setting defaults to 60 so Manifold Garden does not
render hundreds of unused frames while both games are active.

For the actual linked mode, run:

```powershell
./Launch-LinkedGames.ps1
```

RTX Sandbox starts in a temporary `ExternalBridge` world: it does not load or
write a normal save, generate terrain, or draw an environment. Right-click in
Manifold Garden to raycast from the center camera. The first hit establishes an
oriented one-unit bridge grid on the real hit surface; later hits on bridge
blocks use the hit face normal, so floor, wall and edge placement stay adjacent.
Each accepted placement creates a Unity cube with a `BoxCollider` in Manifold
Garden and sends the same grid coordinate plus the currently selected RTX block
id to RTX Sandbox. Manifold Garden also gets its own in-game RTX bridge HUD: it
shows live connection state, a mirrored nine-slot RTX hotbar with selected slot,
the selected RTX block id, placed-block count, right-click controls,
placement/occupancy errors and RTX commit acknowledgement.
The center crosshair changes state with the link, so interaction feedback does
not depend on watching either game's console.
