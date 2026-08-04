using System.Numerics;

namespace RTXSandbox.ModAPI;

public interface IVoxelWorld
{
    /// <summary>Legacy horizontal GPU window width; X coordinates themselves are unbounded.</summary>
    int Width { get; }
    int Height { get; }
    /// <summary>Legacy horizontal GPU window depth; Z coordinates themselves are unbounded.</summary>
    int Depth { get; }
    int MinimumY { get; }
    int MaximumYExclusive { get; }
    int Seed { get; }
    string GenerationPreset { get; }
    string GenerationBiome { get; }
    ushort GetBlock(int x, int y, int z);
    bool SetBlock(int x, int y, int z, ushort blockId);
    bool IsInside(int x, int y, int z);
}

public readonly struct MicroVoxelData
{
    public Vector3 Minimum { get; }
    public Vector3 Maximum { get; }
    public ushort MaterialId { get; }

    public MicroVoxelData(Vector3 minimum, Vector3 maximum, ushort materialId)
    {
        Minimum = minimum;
        Maximum = maximum;
        MaterialId = materialId;
    }
}

/// <summary>Optional precision-building service available through IModContext.GetService.</summary>
public interface IMicroVoxelService
{
    bool Enabled { get; set; }
    int GridDivisor { get; }
    IReadOnlyList<int> SupportedDivisors { get; }
    string Status { get; }
    void SetGridDivisor(int divisor);
    bool PaintCell(
        int worldX,
        int worldY,
        int worldZ,
        int divisor,
        int cellX,
        int cellY,
        int cellZ,
        ushort materialId,
        bool force = false);
    IReadOnlyList<MicroVoxelData> GetCell(int worldX, int worldY, int worldZ);
    bool Undo();
    bool Redo();
}
