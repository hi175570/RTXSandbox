#pragma once

// ImGui.NET 1.91.6.1 exposes ImTextureID as IntPtr. Dear ImGui 1.91.6
// defaults the native type to ImU64, which is ABI-compatible on 64-bit targets
// but corrupts ImDrawCmd and ImFontAtlas calls on 32-bit Android. Native cimgui
// builds for RTX Sandbox must include this file through IMGUI_USER_CONFIG.
#undef NDEBUG
#define ImTextureID void*
