using System.Numerics;
using BepInEx;
using BepInEx.NET.Common;
using HarmonyLib;
using RTXSandbox.Engine;

namespace RTXSandbox.ExampleMod;

[BepInPlugin(PluginId, PluginName, PluginVersion)]
public sealed class ExamplePlugin : BasePlugin
{
    public const string PluginId = "rtxsandbox.example-mod";
    public const string PluginName = "RTX Sandbox Direct Hook Example";
    public const string PluginVersion = "2.0.0";
    public const string BlockKey = "examplemod:signal_block";

    private static ExamplePlugin? _instance;

    public override void Load()
    {
        _instance = this;
        HarmonyInstance.Patch(
            AccessTools.Method(typeof(SandboxGame), "OnLoad")
                ?? throw new MissingMethodException(nameof(SandboxGame), "OnLoad"),
            postfix: new HarmonyMethod(typeof(ExamplePlugin), nameof(GameLoaded)));
        Log.LogInfo("Direct SandboxGame Harmony hook installed; no Mod API is used.");
    }

    private static void GameLoaded(SandboxGame __instance)
    {
        if (__instance.Blocks.TryGet(BlockKey, out _)) return;
        __instance.Blocks.Register(new BlockDefinition(
            BlockKey,
            "Example Mod Signal Block",
            new Vector3(0.82f, 0.06f, 0.32f),
            roughness: 0.22f,
            metallic: 0.35f,
            emission: new Vector3(5.5f, 0.08f, 0.9f),
            category: "Example Mod",
            tags: new[] { "example", "mod", "signal" },
            surfacePattern: BlockSurfacePattern.Circuit));
        __instance.Blocks.NotifyChanged();
        _instance?.Log.LogInfo($"Registered {BlockKey} by directly accessing SandboxGame.Blocks.");
    }
}
