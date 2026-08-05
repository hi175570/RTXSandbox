namespace RTXSandbox.Infrastructure;

/// <summary>
/// Central ownership for work that may be committed on the game/render thread.
/// Count limits are enforced now; millisecond and byte targets are reported so later
/// adaptive scheduling can tune them without scattering magic numbers across systems.
/// </summary>
internal sealed record FrameWorkBudget(
    double ChunkCommitBudgetMilliseconds,
    double ColliderCommitBudgetMilliseconds,
    double GpuUploadBudgetMilliseconds,
    double DxrUpdateBudgetMilliseconds,
    int MaximumChunkCommitsPerFrame,
    int MaximumColliderCommitsPerFrame,
    int MaximumUploadsPerFrame,
    long MaximumUploadBytesPerFrame,
    int MaximumSaveSnapshotsPerFrame)
{
    public static FrameWorkBudget Default { get; } = new(
        ChunkCommitBudgetMilliseconds: 1.0,
        ColliderCommitBudgetMilliseconds: 1.0,
        GpuUploadBudgetMilliseconds: 1.5,
        DxrUpdateBudgetMilliseconds: 1.5,
        MaximumChunkCommitsPerFrame: 1,
        MaximumColliderCommitsPerFrame: 1,
        MaximumUploadsPerFrame: 1,
        MaximumUploadBytesPerFrame: 16L * 1024 * 1024,
        MaximumSaveSnapshotsPerFrame: 1);
}
