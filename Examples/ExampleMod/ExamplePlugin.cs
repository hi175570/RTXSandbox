using System.Numerics;
using BepInEx;
using BepInEx.NET.Common;
using RTXSandbox.ModAPI;

namespace RTXSandbox.ExampleMod;

[BepInPlugin(PluginId, PluginName, PluginVersion)]
public sealed class ExamplePlugin : BasePlugin
{
    public const string PluginId = "rtxsandbox.example-mod";
    public const string PluginName = "RTX Sandbox Example Mod";
    public const string PluginVersion = "1.0.0";
    public const string BlockKey = "examplemod:signal_block";

    public override void Load()
    {
        SandboxApi.WhenReady(context =>
        {
            context.Blocks.Register(new BlockDefinition(
                BlockKey,
                "Example Mod Signal Block",
                new Vector3(0.82f, 0.06f, 0.32f),
                roughness: 0.22f,
                metallic: 0.35f,
                emission: new Vector3(5.5f, 0.08f, 0.9f),
                category: "Example Mod",
                tags: new[] { "example", "mod", "signal" },
                surfacePattern: BlockSurfacePattern.Circuit));
            context.Events.WorldReady += world =>
                context.Log.Info($"{PluginName} received WorldReady for {world.Width}x{world.Height}x{world.Depth}.");
            context.Log.Info($"Registered {BlockKey} through RTXSandbox.ModAPI.");
        });
    }
}
