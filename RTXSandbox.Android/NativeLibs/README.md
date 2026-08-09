# Android cimgui binaries

`libcimgui.so` is built from the MIT-licensed
[`cimgui/cimgui`](https://github.com/cimgui/cimgui) tag `1.91.6dock`
(commit `970c614802935f51f451aa21ae06e838bdcf9349`) and Dear ImGui commit
`947aa9c9722bd6ff740dd757da609ff41f4d3ba3`.

The ABI matches the `ImGui.NET` 1.91.6.1 managed bindings used by the desktop
game. Binaries are built with Android NDK 27.3 for API 26:

- `arm64-v8a/libcimgui.so`
- `armeabi-v7a/libcimgui.so`
- `x86/libcimgui.so`
- `x86_64/libcimgui.so`

All four binaries use the static C++ runtime and 16 KiB-compatible ELF load
segment alignment. They depend only on Android system `libm`, `libdl`, and
`libc`, so every ABI produced by the CI matrix contains a usable ImGui native
backend instead of a build-only placeholder.

All rebuilds must include `cimconfig-android.h` through `IMGUI_USER_CONFIG`.
It keeps `ImTextureID` pointer-sized to match ImGui.NET. Without this override,
Dear ImGui 1.91.6 uses a 64-bit texture ID on 32-bit Android; that changes the
native draw-command ABI and causes an `OverflowException` while rendering the
shared UI. The x86 binary has been executed on the API 30 emulator after this
fix; the ARMv7 binary is build/package validated because the emulator cannot
execute that ABI.

These files provide the native cimgui API only. RTX Sandbox owns its GLES2
renderer and Android input bridge in `Game/AndroidImGuiController.cs`.
