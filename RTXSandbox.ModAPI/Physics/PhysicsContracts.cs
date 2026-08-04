namespace RTXSandbox.ModAPI;

/// <summary>
/// Structural-grid physics service. Structural fracture is a world rule rather than a
/// user-selectable simulation mode; mods can request reevaluation and inspect the active backend.
/// </summary>
public interface IVoxelPhysicsService
{
    string ModeName { get; }
    string BackendName { get; }
    int ActiveGridCount { get; }
    int ChangesLastStep { get; }
    int SimulationSubstepsLastStep { get; }
    string Status { get; }
    void RequestEvaluation();
}
