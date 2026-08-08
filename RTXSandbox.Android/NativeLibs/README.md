# Android cimgui binaries

`libcimgui.so` is built from the MIT-licensed
[`cimgui/cimgui`](https://github.com/cimgui/cimgui) tag `1.91.6dock`
(commit `970c614802935f51f451aa21ae06e838bdcf9349`) and Dear ImGui commit
`947aa9c9722bd6ff740dd757da609ff41f4d3ba3`.

The ABI matches the `ImGui.NET` 1.91.6.1 managed bindings used by the desktop
game. Binaries are built with Android NDK 27.3 for API 26:

- `arm64-v8a/libcimgui.so`
- `x86_64/libcimgui.so`

These files provide the native cimgui API only. RTX Sandbox owns its GLES2
renderer and Android input bridge in `Game/AndroidImGuiController.cs`.
