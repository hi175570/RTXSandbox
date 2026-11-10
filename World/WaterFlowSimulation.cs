using RTXSandbox.Engine;

namespace RTXSandbox.World;

/// <summary>
/// Budgeted finite-volume water simulation. Each water cell stores 1..16 units;
/// transfers conserve that volume, prefer gravity, and locally equalize connected
/// surfaces without scanning an entire ocean.
/// </summary>
public sealed class WaterFlowSimulation : IDisposable
{
    private const float StepSeconds = 0.05f;
    private const int MaximumChangesPerStep = 64;
    private const int MaximumCellsPerStep = 192;
    private const int MaximumPressureComponentCells = 512;
    private const int MaximumDisplacementSearchCells = 2048;

    private static readonly (int X, int Y, int Z)[] HorizontalDirections =
    {
        (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1),
    };

    private static readonly (int X, int Y, int Z)[] NeighborDirections =
    {
        (0, -1, 0), (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1), (0, 1, 0),
    };

    private readonly VoxelWorld _world;
    private readonly Queue<Cell> _pending = new();
    private readonly HashSet<Cell> _queued = new();
    private readonly Queue<Cell> _urgent = new();
    private readonly HashSet<Cell> _urgentQueued = new();
    private readonly Queue<DisplacedWater> _displaced = new();
    private float _accumulator;
    private bool _applying;
    private bool _disposed;

    public int PendingCellCount => _pending.Count + _displaced.Count;

    public WaterFlowSimulation(VoxelWorld world)
    {
        _world = world;
        _world.DetailedVoxelChanged += OnVoxelChanged;
        _world.FluidDisplaced += OnFluidDisplaced;
    }

    public void Update(float elapsedSeconds)
    {
        if (_disposed || elapsedSeconds <= 0f) return;
        _accumulator += Math.Min(elapsedSeconds, StepSeconds * 2f);
        if (_accumulator < StepSeconds) return;
        _accumulator -= StepSeconds;

        int changes = 0;
        int displacedCount = _displaced.Count;
        while (displacedCount-- > 0 && _displaced.TryDequeue(out DisplacedWater spill) &&
               changes < MaximumChangesPerStep)
        {
            byte remaining = RedistributeDisplacedWater(spill.Origin, spill.Amount, ref changes);
            if (remaining > 0) _displaced.Enqueue(spill with { Amount = remaining });
        }

        int urgentWork = Math.Min(_urgent.Count, MaximumCellsPerStep);
        int urgentProcessed = 0;
        while (urgentProcessed < urgentWork && _urgent.TryDequeue(out Cell urgentCell) &&
               changes < MaximumChangesPerStep)
        {
            urgentProcessed++;
            if (!_urgentQueued.Remove(urgentCell)) continue;
            _queued.Remove(urgentCell); // invalidate a stale normal-priority copy
            ProcessCell(urgentCell, ref changes);
        }

        int workCount = Math.Min(_pending.Count, MaximumCellsPerStep - urgentProcessed);
        while (workCount-- > 0 && _pending.TryDequeue(out Cell cell) && changes < MaximumChangesPerStep)
        {
            if (!_queued.Remove(cell)) continue;
            ProcessCell(cell, ref changes);
        }
    }

    public void Reset()
    {
        _pending.Clear();
        _queued.Clear();
        _urgent.Clear();
        _urgentQueued.Clear();
        _displaced.Clear();
        _accumulator = 0f;
    }

    /// <summary>
    /// Applies a small, volume-conserving surface disturbance. It changes only a few
    /// 1/16-level cells, so every renderer sees the same ripple through shared fluid
    /// levels without running a second GPU water simulation.
    /// </summary>
    public int Disturb(System.Numerics.Vector3 position, float strength)
    {
        int centerX = (int)MathF.Floor(position.X);
        int centerZ = (int)MathF.Floor(position.Z);
        int startY = Math.Clamp((int)MathF.Floor(position.Y), VoxelWorld.MinimumY, VoxelWorld.MaximumY - 1);
        Cell? center = null;
        for (int y = startY + 1; y >= startY - 3; y--)
        {
            var candidate = new Cell(centerX, y, centerZ);
            if (!IsWater(candidate)) continue;
            center = candidate;
            break;
        }
        if (!center.HasValue) return 0;

        int pulses = Math.Clamp((int)MathF.Ceiling(MathF.Abs(strength) * 0.35f), 1, 4);
        int moved = 0;
        int rotation = PositiveHash(center.Value) & 3;
        for (int index = 0; index < pulses; index++)
        {
            Cell donor = center.Value;
            if (Amount(donor) <= 1) break;
            (int dx, int _, int dz) = HorizontalDirections[(index + rotation) & 3];
            Cell target = new(donor.X + dx, donor.Y, donor.Z + dz);
            if (!CanHoldWater(target) || Amount(target) >= VoxelWorld.FluidCapacity) continue;
            ApplyTransfer(donor, target, 1);
            moved++;
        }
        return moved;
    }

    private void OnVoxelChanged(VoxelChangedEvent change)
    {
        if (_applying) return;
        var cell = new Cell(change.X, change.Y, change.Z);
        // Excavation must not wait behind the normal ocean equalisation queue.
        // In particular, removing a block below water must refill that cavity on
        // the next simulation step instead of leaving a long-lived air pocket.
        // Ordinary water placement stays in the budgeted queue and cannot flood
        // this small priority lane while a pool is being constructed.
        bool excavation = change.OldBlockId != 0 && change.NewBlockId == 0;
        EnqueueNeighborhood(cell, urgent: excavation);
    }

    private void OnFluidDisplaced(FluidDisplacementEvent displacement)
    {
        if (_applying || displacement.Amount == 0) return;
        var origin = new Cell(displacement.X, displacement.Y, displacement.Z);
        _displaced.Enqueue(new DisplacedWater(origin, displacement.Amount));
        EnqueueNeighborhood(origin);
    }

    private bool TryFall(Cell cell, ref int changes)
    {
        byte sourceAmount = Amount(cell);
        if (sourceAmount == 0) return false;
        Cell below = new(cell.X, cell.Y - 1, cell.Z);
        if (!CanHoldWater(below)) return false;
        byte targetAmount = Amount(below);
        int capacity = VoxelWorld.FluidCapacity - targetAmount;
        if (capacity <= 0) return false;
        byte transfer = (byte)Math.Min(sourceAmount, capacity);
        ApplyTransfer(cell, below, transfer);
        changes += 2;
        return true;
    }

    private void ProcessCell(Cell cell, ref int changes)
    {
        if (!IsWater(cell)) return;
        if (TryFall(cell, ref changes) || TryEqualizeHorizontal(cell, ref changes)) return;
        TryBalanceConnectedSurfaces(cell, ref changes);
    }

    private bool TryEqualizeHorizontal(Cell cell, ref int changes)
    {
        byte sourceAmount = Amount(cell);
        if (sourceAmount <= 1) return false;
        int offset = PositiveHash(cell) & 3;
        for (int index = 0; index < HorizontalDirections.Length; index++)
        {
            (int dx, int _, int dz) = HorizontalDirections[(index + offset) & 3];
            Cell neighbor = new(cell.X + dx, cell.Y, cell.Z + dz);
            if (!CanHoldWater(neighbor)) continue;
            byte neighborAmount = Amount(neighbor);
            int difference = sourceAmount - neighborAmount;
            if (difference <= 1) continue;
            // A one-unit film has surface tension and does not expand forever.
            if (neighborAmount == 0 && sourceAmount <= 2) continue;
            byte transfer = (byte)Math.Clamp(difference / 2, 1, 4);
            ApplyTransfer(cell, neighbor, transfer);
            changes += 2;
            return true;
        }
        return false;
    }

    private bool TryBalanceConnectedSurfaces(Cell start, ref int changes)
    {
        var component = new List<Cell>();
        var visited = new HashSet<Cell> { start };
        var search = new Queue<Cell>();
        search.Enqueue(start);
        while (search.TryDequeue(out Cell cell))
        {
            if (!IsWater(cell)) continue;
            component.Add(cell);
            if (component.Count > MaximumPressureComponentCells) return false;
            foreach ((int dx, int dy, int dz) in NeighborDirections)
            {
                Cell neighbor = new(cell.X + dx, cell.Y + dy, cell.Z + dz);
                if (visited.Add(neighbor) && IsWater(neighbor)) search.Enqueue(neighbor);
            }
        }

        Cell donor = default;
        Cell receiver = default;
        float highestSurface = float.NegativeInfinity;
        float lowestSurface = float.PositiveInfinity;
        bool hasDonor = false;
        bool hasReceiver = false;
        foreach (Cell cell in component)
        {
            Cell above = new(cell.X, cell.Y + 1, cell.Z);
            if (IsWater(above)) continue;
            byte amount = Amount(cell);
            float surface = cell.Y + amount / (float)VoxelWorld.FluidCapacity;
            if (amount > 0 && surface > highestSurface)
            {
                highestSurface = surface;
                donor = cell;
                hasDonor = true;
            }

            Cell target;
            float receivingSurface;
            if (amount < VoxelWorld.FluidCapacity)
            {
                target = cell;
                receivingSurface = surface;
            }
            else if (CanHoldWater(above))
            {
                target = above;
                receivingSurface = cell.Y + 1f;
            }
            else
            {
                continue;
            }
            if (receivingSurface < lowestSurface)
            {
                lowestSurface = receivingSurface;
                receiver = target;
                hasReceiver = true;
            }
        }

        if (!hasDonor || !hasReceiver || donor == receiver ||
            highestSurface - lowestSurface <= 1f / VoxelWorld.FluidCapacity) return false;
        byte receiverAmount = Amount(receiver);
        byte transfer = (byte)Math.Clamp(
            (int)MathF.Ceiling((highestSurface - lowestSurface) * VoxelWorld.FluidCapacity * 0.5f),
            1,
            Math.Min(4, VoxelWorld.FluidCapacity - receiverAmount));
        transfer = Math.Min(transfer, Amount(donor));
        if (transfer == 0) return false;
        ApplyTransfer(donor, receiver, transfer);
        changes += 2;
        return true;
    }

    private byte RedistributeDisplacedWater(Cell origin, byte amount, ref int changes)
    {
        if (amount == 0) return 0;
        Cell below = new(origin.X, origin.Y - 1, origin.Z);
        if (CanHoldWater(below))
        {
            byte accepted = (byte)Math.Min(amount, VoxelWorld.FluidCapacity - Amount(below));
            if (accepted > 0)
            {
                SetAmount(below, (byte)(Amount(below) + accepted));
                amount -= accepted;
                changes++;
                EnqueueNeighborhood(below);
                if (amount == 0) return 0;
            }
        }

        List<Cell> outlets = FindDisplacementOutlets(origin);
        // First make the thinnest possible film, then repeatedly fill the lowest
        // hydraulic surface. A covered water block therefore visibly spills out.
        foreach (Cell outlet in outlets)
        {
            if (amount == 0 || changes >= MaximumChangesPerStep) break;
            if (Amount(outlet) != 0) continue;
            SetAmount(outlet, 1);
            amount--;
            changes++;
            EnqueueNeighborhood(outlet);
        }
        while (amount > 0 && changes < MaximumChangesPerStep)
        {
            Cell? target = outlets
                .Where(CanHoldWater)
                .Where(cell => Amount(cell) < VoxelWorld.FluidCapacity)
                .OrderBy(cell => cell.Y + Amount(cell) / (float)VoxelWorld.FluidCapacity)
                .ThenBy(cell => DistanceSquared(cell, origin))
                .Cast<Cell?>()
                .FirstOrDefault();
            if (!target.HasValue) break;
            byte accepted = (byte)Math.Min(amount, VoxelWorld.FluidCapacity - Amount(target.Value));
            SetAmount(target.Value, (byte)(Amount(target.Value) + accepted));
            amount -= accepted;
            changes++;
            EnqueueNeighborhood(target.Value);
        }
        return amount;
    }

    private List<Cell> FindDisplacementOutlets(Cell origin)
    {
        var outlets = new HashSet<Cell>();
        var visited = new HashSet<Cell> { origin };
        var search = new Queue<Cell>();
        foreach ((int dx, int dy, int dz) in NeighborDirections)
        {
            Cell neighbor = new(origin.X + dx, origin.Y + dy, origin.Z + dz);
            if (visited.Add(neighbor)) search.Enqueue(neighbor);
        }

        while (search.TryDequeue(out Cell cell) && visited.Count <= MaximumDisplacementSearchCells)
        {
            if (!IsLoaded(cell)) continue;
            if (IsWater(cell))
            {
                if (Amount(cell) < VoxelWorld.FluidCapacity) outlets.Add(cell);
                foreach ((int dx, int dy, int dz) in NeighborDirections)
                {
                    Cell neighbor = new(cell.X + dx, cell.Y + dy, cell.Z + dz);
                    if (visited.Add(neighbor)) search.Enqueue(neighbor);
                }
            }
            else if (CanHoldWater(cell))
            {
                outlets.Add(cell);
            }
        }
        return outlets
            .OrderBy(cell => cell.Y)
            .ThenBy(cell => DistanceSquared(cell, origin))
            .ToList();
    }

    private void ApplyTransfer(Cell source, Cell target, byte amount)
    {
        byte sourceAmount = Amount(source);
        byte targetAmount = Amount(target);
        amount = Math.Min(amount, sourceAmount);
        amount = (byte)Math.Min(amount, VoxelWorld.FluidCapacity - targetAmount);
        if (amount == 0) return;
        SetAmount(source, (byte)(sourceAmount - amount));
        SetAmount(target, (byte)(targetAmount + amount));
        EnqueueNeighborhood(source);
        EnqueueNeighborhood(target);
    }

    private void SetAmount(Cell cell, byte amount)
    {
        _applying = true;
        try
        {
            _world.SetFluidAmount(cell.X, cell.Y, cell.Z, amount);
        }
        finally
        {
            _applying = false;
        }
    }

    private byte Amount(Cell cell) => _world.GetFluidAmount(cell.X, cell.Y, cell.Z);
    private bool IsWater(Cell cell) => Amount(cell) > 0;

    private bool CanHoldWater(Cell cell)
    {
        if (!IsLoaded(cell)) return false;
        ushort existing = _world.GetLoadedBlockId(cell.X, cell.Y, cell.Z);
        return existing == (ushort)VoxelMaterial.Water || _world.IsReplaceableMaterial(existing);
    }

    private bool IsLoaded(Cell cell) =>
        _world.IsInside(cell.X, cell.Y, cell.Z) &&
        _world.IsChunkLoaded(ChunkCoordinate.FromVoxel(cell.X, cell.Z));

    private void EnqueueNeighborhood(Cell cell, bool urgent = false)
    {
        EnqueueIfWater(cell, urgent);
        foreach ((int dx, int dy, int dz) in NeighborDirections)
            EnqueueIfWater(new Cell(cell.X + dx, cell.Y + dy, cell.Z + dz), urgent);
    }

    private void EnqueueIfWater(Cell cell, bool urgent = false)
    {
        if (!IsWater(cell)) return;
        if (urgent)
        {
            if (_urgentQueued.Add(cell)) _urgent.Enqueue(cell);
            return;
        }
        if (_queued.Add(cell)) _pending.Enqueue(cell);
    }

    private static int DistanceSquared(Cell left, Cell right)
    {
        int dx = left.X - right.X;
        int dy = left.Y - right.Y;
        int dz = left.Z - right.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static int PositiveHash(Cell cell) =>
        (cell.X * 73856093 ^ cell.Y * 19349663 ^ cell.Z * 83492791) & int.MaxValue;

    public void Dispose()
    {
        if (_disposed) return;
        _world.DetailedVoxelChanged -= OnVoxelChanged;
        _world.FluidDisplaced -= OnFluidDisplaced;
        Reset();
        _disposed = true;
    }

    private readonly record struct Cell(int X, int Y, int Z);
    private readonly record struct DisplacedWater(Cell Origin, byte Amount);
}
