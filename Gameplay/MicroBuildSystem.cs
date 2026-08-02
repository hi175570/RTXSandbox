using System.Numerics;
using RTXSandbox.ModAPI;
using RTXSandbox.World;

namespace RTXSandbox.Gameplay;

public readonly record struct PrecisionHit(
    int X,
    int Y,
    int Z,
    Vector3 Position,
    Vector3 Normal,
    float Distance,
    ushort MaterialId,
    bool IsMicro);

public sealed class MicroBuildSystem : IMicroVoxelService
{
    private const int MaximumHistory = 64;
    private static readonly int[] SupportedDivisorsInternal = { 2, 4, 6, 8, 16, 32, 64 };

    private readonly VoxelWorld _world;
    private readonly BlockRegistry _blocks;
    private readonly Func<Vector3, Vector3, bool>? _wouldOverlapPlayer;
    private readonly Stack<MicroEditCommand> _undo = new();
    private readonly Stack<MicroEditCommand> _redo = new();
    private int _gridIndex = 4;

    public bool Enabled { get; set; }
    public int GridDivisor => SupportedDivisorsInternal[_gridIndex];
    public IReadOnlyList<int> SupportedDivisors => SupportedDivisorsInternal;
    public int UndoDepth => _undo.Count;
    public int RedoDepth => _redo.Count;
    public string Status { get; private set; } = "Precision mode ready";

    public MicroBuildSystem(
        VoxelWorld world,
        BlockRegistry blocks,
        Func<Vector3, Vector3, bool>? wouldOverlapPlayer = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _blocks = blocks ?? throw new ArgumentNullException(nameof(blocks));
        _wouldOverlapPlayer = wouldOverlapPlayer;
    }

    public void SetGridDivisor(int divisor)
    {
        int index = Array.IndexOf(SupportedDivisorsInternal, divisor);
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(divisor));
        _gridIndex = index;
        Status = $"Precision grid: 1/{GridDivisor}";
    }

    public void CycleGrid(int direction)
    {
        if (direction == 0) return;
        _gridIndex = (_gridIndex + Math.Sign(direction) + SupportedDivisorsInternal.Length) % SupportedDivisorsInternal.Length;
        Status = $"Precision grid: 1/{GridDivisor}";
    }

    public bool Place(PrecisionHit hit, ushort materialId)
    {
        BlockDefinition material = _blocks.Get(materialId);
        if (!material.IsPlaceable || materialId == 0 || materialId == BlockRegistry.UnknownBlockId)
        {
            Status = "Selected material cannot be placed";
            return false;
        }

        Vector3 targetPoint = hit.Position + hit.Normal * 0.0025f;
        (int x, int y, int z, int cellX, int cellY, int cellZ) = GridCellAt(targetPoint);
        if (!_world.IsInside(x, y, z))
        {
            Status = "Precision placement is outside the world";
            return false;
        }

        ushort regular = _world.GetBlockId(x, y, z);
        if (regular != 0 && !_blocks.Get(regular).IsReplaceable)
        {
            Status = "Precision placement is blocked by a full voxel";
            return false;
        }

        float scale = 1f / GridDivisor;
        Vector3 boxMinimum = new(x + cellX * scale, y + cellY * scale, z + cellZ * scale);
        Vector3 boxMaximum = boxMinimum + new Vector3(scale);
        if (material.IsCollidable && _wouldOverlapPlayer?.Invoke(boxMinimum, boxMaximum) == true)
        {
            Status = "Precision placement would intersect the player";
            return false;
        }

        MicroVoxelCellSnapshot before = _world.CaptureCell(x, y, z);
        MicroVoxelContainer container = before.MicroVoxels is not null
            ? new MicroVoxelContainer(before.MicroVoxels)
            : new MicroVoxelContainer(GridDivisor);
        container.PaintCell(GridDivisor, cellX, cellY, cellZ, materialId);
        _world.SetMicroVoxels(x, y, z, container);
        MicroVoxelCellSnapshot after = _world.CaptureCell(x, y, z);
        Push(new MicroEditCommand("Place micro voxel", before, after));
        Status = $"Placed {material.DisplayName} at 1/{GridDivisor}";
        return true;
    }

    public bool Erase(PrecisionHit hit)
    {
        Vector3 targetPoint = hit.Position - hit.Normal * 0.0025f;
        (int x, int y, int z, int cellX, int cellY, int cellZ) = GridCellAt(targetPoint);
        if (!_world.IsInside(x, y, z)) return false;

        MicroVoxelCellSnapshot before = _world.CaptureCell(x, y, z);
        MicroVoxelContainer? container;
        if (before.MicroVoxels is not null)
        {
            container = new MicroVoxelContainer(before.MicroVoxels);
        }
        else if (before.RegularBlockId != 0)
        {
            BlockDefinition regular = _blocks.Get(before.RegularBlockId);
            if (regular.IsUnbreakable)
            {
                Status = "That material is unbreakable";
                return false;
            }
            container = new MicroVoxelContainer(GridDivisor);
            container.Fill(before.RegularBlockId);
        }
        else
        {
            Status = "No micro geometry at the target";
            return false;
        }

        container.PaintCell(GridDivisor, cellX, cellY, cellZ, 0);
        _world.SetMicroVoxels(x, y, z, container.IsEmpty ? null : container);
        if (before.RegularBlockId != 0 && container.IsEmpty)
        {
            _world.SetBlockId(x, y, z, 0);
        }
        MicroVoxelCellSnapshot after = _world.CaptureCell(x, y, z);
        Push(new MicroEditCommand("Erase micro voxel", before, after));
        Status = $"Removed 1/{GridDivisor} voxel";
        return true;
    }

    public bool PaintCell(
        int worldX,
        int worldY,
        int worldZ,
        int divisor,
        int cellX,
        int cellY,
        int cellZ,
        ushort materialId,
        bool force = false)
    {
        if (!_world.IsInside(worldX, worldY, worldZ)) return false;
        if (divisor < 1 || divisor > MicroVoxelContainer.MaximumResolution ||
            cellX < 0 || cellX >= divisor || cellY < 0 || cellY >= divisor || cellZ < 0 || cellZ >= divisor)
            throw new ArgumentOutOfRangeException(nameof(divisor), "Micro cell coordinates must be inside the requested divisor.");
        if (materialId != 0)
        {
            if (materialId >= BlockRegistry.MaterialCapacity || materialId == BlockRegistry.UnknownBlockId) return false;
            BlockDefinition material = _blocks.Get(materialId);
            if (!material.IsPlaceable && !force) return false;
        }

        MicroVoxelCellSnapshot before = _world.CaptureCell(worldX, worldY, worldZ);
        if (before.RegularBlockId != 0 && _blocks.Get(before.RegularBlockId).IsUnbreakable && !force) return false;
        MicroVoxelContainer container;
        if (before.MicroVoxels is not null)
        {
            container = new MicroVoxelContainer(before.MicroVoxels);
        }
        else
        {
            container = new MicroVoxelContainer(divisor);
            if (before.RegularBlockId != 0) container.Fill(before.RegularBlockId);
        }

        float scale = 1f / divisor;
        Vector3 minimum = new(
            worldX + cellX * scale,
            worldY + cellY * scale,
            worldZ + cellZ * scale);
        if (!force && materialId != 0 && _blocks.Get(materialId).IsCollidable &&
            _wouldOverlapPlayer?.Invoke(minimum, minimum + new Vector3(scale)) == true)
            return false;

        container.PaintCell(divisor, cellX, cellY, cellZ, materialId);
        _world.SetMicroVoxels(worldX, worldY, worldZ, container.IsEmpty ? null : container);
        MicroVoxelCellSnapshot after = _world.CaptureCell(worldX, worldY, worldZ);
        if (before == after) return false;
        Push(new MicroEditCommand("Mod precision paint", before, after));
        Status = $"Painted mod micro cell at 1/{divisor}";
        return true;
    }

    public IReadOnlyList<MicroVoxelData> GetCell(int worldX, int worldY, int worldZ)
    {
        if (!_world.IsInside(worldX, worldY, worldZ)) return Array.Empty<MicroVoxelData>();
        if (_world.TryGetMicroVoxels(worldX, worldY, worldZ, out MicroVoxelContainer? micro))
        {
            return micro!.Boxes
                .Select(box => micro.ToWorldAabb(worldX, worldY, worldZ, box))
                .Select(box => new MicroVoxelData(box.Minimum, box.Maximum, box.MaterialId))
                .ToArray();
        }
        ushort regular = _world.GetBlockId(worldX, worldY, worldZ);
        return regular == 0
            ? Array.Empty<MicroVoxelData>()
            : new[] { new MicroVoxelData(new Vector3(worldX, worldY, worldZ), new Vector3(worldX + 1, worldY + 1, worldZ + 1), regular) };
    }

    public bool Undo()
    {
        if (!_undo.TryPop(out MicroEditCommand? command))
        {
            Status = "Nothing to undo in precision history";
            return false;
        }
        _world.RestoreCell(command.Before);
        _redo.Push(command);
        Status = $"Undo: {command.Name}";
        return true;
    }

    public bool Redo()
    {
        if (!_redo.TryPop(out MicroEditCommand? command))
        {
            Status = "Nothing to redo in precision history";
            return false;
        }
        _world.RestoreCell(command.After);
        _undo.Push(command);
        TrimHistory(_undo);
        Status = $"Redo: {command.Name}";
        return true;
    }

    public void ResetForNewWorld()
    {
        _undo.Clear();
        _redo.Clear();
        Status = "Precision mode ready";
    }

    private (int X, int Y, int Z, int CellX, int CellY, int CellZ) GridCellAt(Vector3 point)
    {
        int x = (int)MathF.Floor(point.X);
        int y = (int)MathF.Floor(point.Y);
        int z = (int)MathF.Floor(point.Z);
        float localX = Math.Clamp(point.X - x, 0f, 0.999999f);
        float localY = Math.Clamp(point.Y - y, 0f, 0.999999f);
        float localZ = Math.Clamp(point.Z - z, 0f, 0.999999f);
        return (
            x,
            y,
            z,
            Math.Clamp((int)MathF.Floor(localX * GridDivisor), 0, GridDivisor - 1),
            Math.Clamp((int)MathF.Floor(localY * GridDivisor), 0, GridDivisor - 1),
            Math.Clamp((int)MathF.Floor(localZ * GridDivisor), 0, GridDivisor - 1));
    }

    private void Push(MicroEditCommand command)
    {
        _undo.Push(command);
        _redo.Clear();
        TrimHistory(_undo);
    }

    private static void TrimHistory(Stack<MicroEditCommand> history)
    {
        if (history.Count <= MaximumHistory) return;
        MicroEditCommand[] keep = history.Take(MaximumHistory).Reverse().ToArray();
        history.Clear();
        foreach (MicroEditCommand command in keep) history.Push(command);
    }

    private sealed record MicroEditCommand(
        string Name,
        MicroVoxelCellSnapshot Before,
        MicroVoxelCellSnapshot After);
}
