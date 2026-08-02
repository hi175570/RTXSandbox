namespace RTXSandbox.ModAPI;

/// <summary>
/// Structural-grid physics service. String modes are retained for source compatibility and keep the public API independent from the game's
/// configuration assembly and allow future mods to introduce custom simulation modes.
/// </summary>
public interface IVoxelPhysicsService
{
    string ModeName { get; }
    int ChangesLastStep { get; }
    string Status { get; }
    bool TrySetMode(string modeName);
    void RequestEvaluation();
}
