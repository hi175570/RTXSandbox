using BepInEx;
using BepInEx.NET.Common;
using System.Runtime.Loader;

[BepInPlugin("rtxsandbox.tests.preloader.plugin", "Preloader verification plugin", "1.0.0")]
public sealed class ProbePlugin : BasePlugin
{
    public override void Load()
    {
        var games = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == "RTXSandbox").ToArray();
        if (games.Length != 1 || AssemblyLoadContext.GetLoadContext(games[0]) != AssemblyLoadContext.Default)
            throw new InvalidOperationException("Patched game has duplicate/wrong assembly identity.");
        object? value = games[0].GetType("RTXSandbox.Tests.PreloaderMarker")?.GetField("Value")?.GetRawConstantValue();
        if (value is not 314159 || AppContext.GetData("RTXSandbox.Probe.Initialized") is not true ||
            AppContext.GetData("RTXSandbox.Probe.Finalized") is not true)
            throw new InvalidOperationException("Preloader metadata or lifecycle verification failed.");
        Console.WriteLine("PRELOADER_PLUGIN_VERIFIED: metadata=314159; initialize/finalize=True; game copies=1; default context=True");
    }
}
