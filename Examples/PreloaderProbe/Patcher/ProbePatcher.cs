using BepInEx.Preloader.Core.Patching;
using Mono.Cecil;
using Mono.Cecil.Cil;

[PatcherPluginInfo("rtxsandbox.tests.preloader", "Preloader integration probe", "1.0.0")]
public sealed class ProbePatcher : BasePatcher
{
    public override void Initialize()
    {
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "RTXSandbox"))
            throw new InvalidOperationException("Game was loaded before patcher Initialize.");
        AppContext.SetData("RTXSandbox.Probe.Initialized", true);
    }

    [TargetAssembly("RTXSandbox.dll")]
    public void Patch(AssemblyDefinition game)
    {
        if (Environment.GetEnvironmentVariable("RTX_SANDBOX_PROBE_FAIL") == "1")
            throw new InvalidOperationException("Deliberate patcher failure for recovery validation.");
        // Add real metadata to the game before CLR load, not a runtime Harmony
        // override. An ordinary plugin verifies this new field after startup.
        var type = new TypeDefinition("RTXSandbox.Tests", "PreloaderMarker",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
            game.MainModule.TypeSystem.Object);
        type.Fields.Add(new FieldDefinition("Value", FieldAttributes.Public | FieldAttributes.Static |
            FieldAttributes.Literal | FieldAttributes.HasDefault, game.MainModule.TypeSystem.Int32) { Constant = 314159 });
        game.MainModule.Types.Add(type);
        var il = game.EntryPoint.Body.GetILProcessor();
        var first = game.EntryPoint.Body.Instructions[0];
        il.InsertBefore(first, il.Create(OpCodes.Ldstr, "PRELOADER_GAME_ENTRY_PATCHED"));
        il.InsertBefore(first, il.Create(OpCodes.Call, game.MainModule.ImportReference(
            typeof(Console).GetMethod(nameof(Console.WriteLine), new[] { typeof(string) })!)));
    }

    public override void Finalizer() => AppContext.SetData("RTXSandbox.Probe.Finalized", true);
}
