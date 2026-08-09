using RTXSandbox.ModAPI;
using RTXSandbox.World;

namespace RTXSandbox.Gameplay;

public enum EditorToolMode
{
    Place,
    Erase,
    Replace,
    Pick,
    SelectA,
    SelectB,
    BrushAdd,
    BrushErase,
    Flatten,
    Explode,
    GridAssign,
    Custom,
}

public sealed class BuildToolSystem
{
    private const int DefaultQueuedEditsPerFrame = 16;
    private const int MaximumHistory = 64;
    private readonly VoxelWorld _world;
    private readonly BlockRegistry _blocks;
    private readonly BuildToolRegistry _customTools;
    private readonly Func<int, int, int, bool>? _wouldOverlapPlayer;
    private readonly Stack<VoxelEditCommand> _undo = new();
    private readonly Stack<VoxelEditCommand> _redo = new();
    private readonly Queue<PendingEditCommand> _pendingEditCommands = new();
    private ushort[]? _clipboard;
    private int _clipboardWidth;
    private int _clipboardHeight;
    private int _clipboardDepth;
    private int _queuedEditCount;

    public EditorToolMode ToolMode { get; set; } = EditorToolMode.Place;
    public ushort SelectedBlockId { get; set; } = (ushort)VoxelMaterial.Stone;
    public int BrushRadius { get; set; } = 2;
    public int ExplosionRadius { get; set; } = 4;
    public VoxelPoint? SelectionA { get; private set; }
    public VoxelPoint? SelectionB { get; private set; }
    public string? SelectedCustomToolKey { get; set; }
    public string Status { get; private set; } = "Editor ready";
    public int UndoDepth => _undo.Count;
    public int RedoDepth => _redo.Count;
    public bool HasClipboard => _clipboard is not null;
    public int PendingEditCount => _queuedEditCount;
    public bool HasPendingEdits => _queuedEditCount > 0;

    public int SelectionVolume
    {
        get
        {
            if (!TryGetSelectionBounds(out VoxelPoint minimum, out VoxelPoint maximum))
            {
                return 0;
            }
            return (maximum.X - minimum.X + 1) *
                   (maximum.Y - minimum.Y + 1) *
                   (maximum.Z - minimum.Z + 1);
        }
    }

    public string ClipboardDescription => _clipboard is null
        ? "Empty"
        : $"{_clipboardWidth} x {_clipboardHeight} x {_clipboardDepth}";
    public bool AutoApplyQueuedEdits { get; set; } = true;

    public BuildToolSystem(
        VoxelWorld world,
        BlockRegistry blocks,
        BuildToolRegistry customTools,
        Func<int, int, int, bool>? wouldOverlapPlayer = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _blocks = blocks ?? throw new ArgumentNullException(nameof(blocks));
        _customTools = customTools ?? throw new ArgumentNullException(nameof(customTools));
        _wouldOverlapPlayer = wouldOverlapPlayer;
    }

    public bool UsePrimary(VoxelHit hit, bool alternate = false)
    {
        ClampOptions();
        return ToolMode switch
        {
            // The base interaction is deliberately Minecraft-like: primary destroys,
            // secondary places. "Place" remains the neutral single-cell tool state.
            EditorToolMode.Place => Erase(hit),
            EditorToolMode.Erase => Erase(hit),
            EditorToolMode.Replace => Replace(hit),
            EditorToolMode.Pick => Pick(hit),
            EditorToolMode.SelectA => SetSelectionA(hit.X, hit.Y, hit.Z),
            EditorToolMode.SelectB => SetSelectionB(hit.X, hit.Y, hit.Z),
            EditorToolMode.BrushAdd => Sphere(hit.Adjacent, BrushRadius, SelectedBlockId, "Add sphere"),
            EditorToolMode.BrushErase => Sphere((hit.X, hit.Y, hit.Z), BrushRadius, 0, "Erase sphere"),
            EditorToolMode.Flatten => FlattenAt(hit.X, hit.Y, hit.Z),
            EditorToolMode.Explode => Explode(hit.X, hit.Y, hit.Z, ExplosionRadius),
            EditorToolMode.Custom => UseCustom(hit, alternate),
            _ => false,
        };
    }

    public bool QuickPlace(VoxelHit hit) => Place(hit);

    public bool QueueEdit(int x, int y, int z, ushort newBlockId, string? name = null)
    {
        var requested = new Dictionary<VoxelPoint, PendingEdit>();
        AddPending(requested, x, y, z, newBlockId);
        return Commit(requested, name ?? "Queued edit");
    }

    public void ApplyQueuedEdits()
    {
        ApplyQueuedEdits(DefaultQueuedEditsPerFrame);
    }

    public bool ApplyQueuedEdits(int maximumEdits)
    {
        if (maximumEdits <= 0 || _pendingEditCommands.Count == 0)
        {
            return false;
        }

        int remaining = maximumEdits;
        bool changed = false;
        while (remaining > 0 && _pendingEditCommands.Count > 0)
        {
            PendingEditCommand command = _pendingEditCommands.Peek();
            while (remaining > 0 && command.NextIndex < command.Edits.Length)
            {
                VoxelEdit edit = command.Edits[command.NextIndex++];
                bool editApplied = edit.IsFluidEdit
                    ? _world.SetFluidAmount(edit.X, edit.Y, edit.Z, edit.NewFluidAmount)
                    : _world.SetBlockId(edit.X, edit.Y, edit.Z, edit.NewBlockId);
                _queuedEditCount = Math.Max(0, _queuedEditCount - 1);
                remaining--;
                if (!editApplied)
                {
                    continue;
                }

                command.AppliedEdits.Add(edit);
                changed = true;
            }

            if (command.NextIndex >= command.Edits.Length)
            {
                _pendingEditCommands.Dequeue();
                IReadOnlyList<VoxelEdit> appliedEdits = command.AppliedEdits;
                if (appliedEdits.Count > 0)
                {
                    PushUndo(new VoxelEditCommand(command.Name, appliedEdits));
                    _redo.Clear();
                }
                Status = appliedEdits.Count == 0
                    ? $"{command.Name}: no editable voxels"
                    : $"{command.Name}: {appliedEdits.Count:N0} voxels";
            }
            else if (changed)
            {
                Status = $"Applying {command.Name}...";
            }
        }

        return changed;
    }

    public bool SetSelectionA(int x, int y, int z)
    {
        if (!_world.IsInside(x, y, z)) return false;
        SelectionA = new VoxelPoint(x, y, z);
        Status = $"Selection A: {SelectionA}";
        return true;
    }

    public bool SetSelectionB(int x, int y, int z)
    {
        if (!_world.IsInside(x, y, z)) return false;
        SelectionB = new VoxelPoint(x, y, z);
        Status = $"Selection B: {SelectionB} ({SelectionVolume:N0} voxels)";
        return true;
    }

    public void ClearSelection()
    {
        SelectionA = null;
        SelectionB = null;
        Status = "Selection cleared";
    }

    public void ResetForNewWorld()
    {
        SelectionA = null;
        SelectionB = null;
        _clipboard = null;
        _clipboardWidth = 0;
        _clipboardHeight = 0;
        _clipboardDepth = 0;
        ClearPendingEditQueue();
        _undo.Clear();
        _redo.Clear();
        Status = "New world ready";
    }

    public bool FillSelection(ushort? blockId = null)
    {
        if (!TryGetSelectionBounds(out VoxelPoint minimum, out VoxelPoint maximum))
        {
            Status = "Set selection A and B first";
            return false;
        }

        ushort fill = blockId ?? SelectedBlockId;
        return ApplyRegion(
            minimum,
            maximum,
            (_, _, _, _) => fill,
            fill == 0 ? "Clear selection" : "Fill selection");
    }

    public bool ReplaceSelection(ushort fromBlockId, ushort? toBlockId = null)
    {
        if (!TryGetSelectionBounds(out VoxelPoint minimum, out VoxelPoint maximum))
        {
            Status = "Set selection A and B first";
            return false;
        }

        ushort replacement = toBlockId ?? SelectedBlockId;
        return ApplyRegion(
            minimum,
            maximum,
            (_, _, _, old) => old == fromBlockId ? replacement : old,
            "Replace selection");
    }

    public bool CopySelection()
    {
        if (!TryGetSelectionBounds(out VoxelPoint minimum, out VoxelPoint maximum))
        {
            Status = "Set selection A and B first";
            return false;
        }

        _clipboardWidth = maximum.X - minimum.X + 1;
        _clipboardHeight = maximum.Y - minimum.Y + 1;
        _clipboardDepth = maximum.Z - minimum.Z + 1;
        _clipboard = new ushort[_clipboardWidth * _clipboardHeight * _clipboardDepth];
        for (int z = 0; z < _clipboardDepth; z++)
        for (int y = 0; y < _clipboardHeight; y++)
        for (int x = 0; x < _clipboardWidth; x++)
        {
            _clipboard[ClipboardIndex(x, y, z)] = _world.GetBlockId(minimum.X + x, minimum.Y + y, minimum.Z + z);
        }
        Status = $"Copied {ClipboardDescription} voxels";
        return true;
    }

    public bool Paste(VoxelPoint anchor, bool includeAir = true)
    {
        if (_clipboard is null)
        {
            Status = "Clipboard is empty";
            return false;
        }

        var requested = new Dictionary<VoxelPoint, PendingEdit>();
        for (int z = 0; z < _clipboardDepth; z++)
        for (int y = 0; y < _clipboardHeight; y++)
        for (int x = 0; x < _clipboardWidth; x++)
        {
            ushort block = _clipboard[ClipboardIndex(x, y, z)];
            if (!includeAir && block == 0) continue;
            AddPending(requested, anchor.X + x, anchor.Y + y, anchor.Z + z, block);
        }
        return Commit(requested, $"Paste {ClipboardDescription}");
    }

    public bool RotateClipboardY()
    {
        if (_clipboard is null)
        {
            Status = "Clipboard is empty";
            return false;
        }

        int oldWidth = _clipboardWidth;
        int oldDepth = _clipboardDepth;
        ushort[] rotated = new ushort[oldWidth * _clipboardHeight * oldDepth];
        int newWidth = oldDepth;
        int newDepth = oldWidth;
        for (int z = 0; z < oldDepth; z++)
        for (int y = 0; y < _clipboardHeight; y++)
        for (int x = 0; x < oldWidth; x++)
        {
            int newX = oldDepth - 1 - z;
            int newZ = x;
            rotated[newX + newWidth * (y + _clipboardHeight * newZ)] = _clipboard[ClipboardIndex(x, y, z)];
        }
        _clipboard = rotated;
        _clipboardWidth = newWidth;
        _clipboardDepth = newDepth;
        Status = $"Clipboard rotated 90 degrees: {ClipboardDescription}";
        return true;
    }

    public bool MirrorClipboardX()
    {
        if (_clipboard is null)
        {
            Status = "Clipboard is empty";
            return false;
        }

        ushort[] mirrored = new ushort[_clipboard.Length];
        for (int z = 0; z < _clipboardDepth; z++)
        for (int y = 0; y < _clipboardHeight; y++)
        for (int x = 0; x < _clipboardWidth; x++)
        {
            mirrored[ClipboardIndex(_clipboardWidth - 1 - x, y, z)] = _clipboard[ClipboardIndex(x, y, z)];
        }
        _clipboard = mirrored;
        Status = $"Clipboard mirrored on X: {ClipboardDescription}";
        return true;
    }

    public bool Explode(int centerX, int centerY, int centerZ, int radius)
    {
        radius = Math.Clamp(radius, 1, 12);
        return Sphere((centerX, centerY, centerZ), radius, 0, $"Explosion radius {radius}");
    }

    public bool Undo()
    {
        if (HasPendingEdits) return false;
        if (!_undo.TryPop(out VoxelEditCommand? command))
        {
            Status = "Nothing to undo";
            return false;
        }

        for (int i = command.Edits.Count - 1; i >= 0; i--)
        {
            VoxelEdit edit = command.Edits[i];
            if (edit.IsFluidEdit)
            {
                _world.SetFluidAmount(edit.X, edit.Y, edit.Z, edit.OldFluidAmount);
            }
            else
            {
                _world.SetBlockId(edit.X, edit.Y, edit.Z, edit.OldBlockId);
            }
        }
        _redo.Push(command);
        Status = $"Undo: {command.Name} ({command.Edits.Count:N0} voxels)";
        return true;
    }

    public bool Redo()
    {
        if (HasPendingEdits) return false;
        if (!_redo.TryPop(out VoxelEditCommand? command))
        {
            Status = "Nothing to redo";
            return false;
        }

        foreach (VoxelEdit edit in command.Edits)
        {
            if (edit.IsFluidEdit)
            {
                _world.SetFluidAmount(edit.X, edit.Y, edit.Z, edit.NewFluidAmount);
            }
            else
            {
                _world.SetBlockId(edit.X, edit.Y, edit.Z, edit.NewBlockId);
            }
        }
        PushUndo(command);
        Status = $"Redo: {command.Name} ({command.Edits.Count:N0} voxels)";
        return true;
    }

    public bool TryGetSelectionBounds(out VoxelPoint minimum, out VoxelPoint maximum)
    {
        if (SelectionA is not VoxelPoint a || SelectionB is not VoxelPoint b)
        {
            minimum = default;
            maximum = default;
            return false;
        }
        minimum = new VoxelPoint(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));
        maximum = new VoxelPoint(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));
        return true;
    }

    private bool Place(VoxelHit hit)
    {
        if (!PlacementRules.IsFarEnough(hit.Distance))
        {
            Status = $"Place voxel: target must be at least {PlacementRules.MinimumEyeDistance:0.0} blocks from the eye";
            return false;
        }
        BlockDefinition hitBlock = _blocks.Get(hit.MaterialId);
        (int x, int y, int z) = hitBlock.IsReplaceable && hit.MaterialId != SelectedBlockId
            ? (hit.X, hit.Y, hit.Z)
            : hit.Adjacent;
        BlockDefinition selectedBlock = _blocks.Get(SelectedBlockId);
        if (SelectedBlockId == (ushort)VoxelMaterial.Water && _world.IsFluid(hit.X, hit.Y, hit.Z))
        {
            bool poured = _world.PourWater(hit.X, hit.Y, hit.Z);
            Status = poured ? "Poured water into the connected volume" : "Water could not be placed here";
            return poured;
        }
        var requested = new Dictionary<VoxelPoint, PendingEdit>();
        AddPending(requested, x, y, z, SelectedBlockId);
        return Commit(requested, "Place voxel");
    }

    private bool Erase(VoxelHit hit)
    {
        var requested = new Dictionary<VoxelPoint, PendingEdit>();
        AddPending(requested, hit.X, hit.Y, hit.Z, 0);
        return Commit(requested, "Erase voxel");
    }

    private bool Replace(VoxelHit hit)
    {
        var requested = new Dictionary<VoxelPoint, PendingEdit>();
        AddPending(requested, hit.X, hit.Y, hit.Z, SelectedBlockId);
        return Commit(requested, "Replace voxel");
    }

    private bool Pick(VoxelHit hit)
    {
        if (hit.MaterialId == 0 || !_blocks.Get(hit.MaterialId).IsPlaceable)
        {
            Status = "That material is not placeable";
            return false;
        }
        SelectedBlockId = hit.MaterialId;
        ToolMode = EditorToolMode.Place;
        Status = $"Picked {_blocks.Get(SelectedBlockId).DisplayName}";
        return true;
    }

    private bool Sphere((int X, int Y, int Z) center, int radius, ushort blockId, string name)
    {
        radius = Math.Clamp(radius, 1, 12);
        var requested = new Dictionary<VoxelPoint, PendingEdit>();
        int radiusSquared = radius * radius;
        for (int z = center.Z - radius; z <= center.Z + radius; z++)
        for (int y = center.Y - radius; y <= center.Y + radius; y++)
        for (int x = center.X - radius; x <= center.X + radius; x++)
        {
            int dx = x - center.X;
            int dy = y - center.Y;
            int dz = z - center.Z;
            if (dx * dx + dy * dy + dz * dz <= radiusSquared)
            {
                AddPending(requested, x, y, z, blockId);
            }
        }
        return Commit(requested, name);
    }

    private bool FlattenAt(int centerX, int targetY, int centerZ)
    {
        var requested = new Dictionary<VoxelPoint, PendingEdit>();
        int radiusSquared = BrushRadius * BrushRadius;
        for (int z = centerZ - BrushRadius; z <= centerZ + BrushRadius; z++)
        for (int x = centerX - BrushRadius; x <= centerX + BrushRadius; x++)
        {
            int dx = x - centerX;
            int dz = z - centerZ;
            if (dx * dx + dz * dz > radiusSquared) continue;

            for (int y = targetY + 1; y < VoxelWorld.MaximumY; y++)
            {
                ushort existing = _world.GetBlockId(x, y, z);
                if (existing != 0 && !_blocks.Get(existing).IsFluid)
                {
                    AddPending(requested, x, y, z, 0);
                }
            }
            AddPending(requested, x, targetY, z, SelectedBlockId);
        }
        return Commit(requested, $"Flatten terrain at Y={targetY}");
    }

    private bool ApplyRegion(
        VoxelPoint minimum,
        VoxelPoint maximum,
        Func<int, int, int, ushort, ushort> transform,
        string name)
    {
        var requested = new Dictionary<VoxelPoint, PendingEdit>();
        for (int z = minimum.Z; z <= maximum.Z; z++)
        for (int y = minimum.Y; y <= maximum.Y; y++)
        for (int x = minimum.X; x <= maximum.X; x++)
        {
            ushort old = _world.GetBlockId(x, y, z);
            AddPending(requested, x, y, z, transform(x, y, z, old));
        }
        return Commit(requested, name);
    }

    private bool UseCustom(VoxelHit hit, bool alternate)
    {
        if (string.IsNullOrWhiteSpace(SelectedCustomToolKey) ||
            !_customTools.TryGet(SelectedCustomToolKey, out BuildToolDefinition? tool))
        {
            Status = "Select a mod tool in the editor panel";
            return false;
        }

        var changes = new Dictionary<VoxelPoint, VoxelEdit>();
        void Record(VoxelChangedEvent change)
        {
            var key = new VoxelPoint(change.X, change.Y, change.Z);
            if (changes.TryGetValue(key, out VoxelEdit previous))
            {
                changes[key] = previous with { NewBlockId = change.NewBlockId };
            }
            else
            {
                changes[key] = new VoxelEdit(
                    change.X,
                    change.Y,
                    change.Z,
                    change.OldBlockId,
                    change.NewBlockId,
                    0,
                    0,
                    false);
            }
        }

        _world.DetailedVoxelChanged += Record;
        try
        {
            tool!.Use(new BuildToolUseContext(
                _world,
                hit.X,
                hit.Y,
                hit.Z,
                (int)hit.Normal.X,
                (int)hit.Normal.Y,
                (int)hit.Normal.Z,
                SelectedBlockId,
                BrushRadius,
                alternate));
        }
        catch (Exception exception)
        {
            Status = $"Tool {tool!.DisplayName} failed: {exception.Message}";
        }
        finally
        {
            _world.DetailedVoxelChanged -= Record;
        }

        List<VoxelEdit> edits = changes.Values.Where(edit => edit.OldBlockId != edit.NewBlockId).ToList();
        if (edits.Count == 0) return false;
        PushUndo(new VoxelEditCommand(tool!.DisplayName, edits));
        _redo.Clear();
        Status = $"{tool.DisplayName}: {edits.Count:N0} voxels";
        return true;
    }

    private void AddPending(Dictionary<VoxelPoint, PendingEdit> requested, int x, int y, int z, ushort blockId)
    {
        if (!_world.IsInside(x, y, z)) return;
        var key = new VoxelPoint(x, y, z);
        requested[key] = new PendingEdit(x, y, z, blockId);
    }

    private bool Commit(Dictionary<VoxelPoint, PendingEdit> requested, string name)
    {
        var edits = new List<VoxelEdit>(requested.Count);
        foreach (PendingEdit pending in requested.Values)
        {
            ushort old = _world.GetBlockId(pending.X, pending.Y, pending.Z);
            BlockDefinition newDefinition = _blocks.Get(pending.NewBlockId);
            bool hasMicro = _world.TryGetMicroVoxels(
                pending.X,
                pending.Y,
                pending.Z,
                out MicroVoxelContainer? micro);
            if (old == pending.NewBlockId && newDefinition.IsFluid && _world.IsFluid(pending.X, pending.Y, pending.Z))
            {
                byte oldFluidAmount = _world.GetFluidAmount(pending.X, pending.Y, pending.Z);
                byte newFluidAmount = (byte)Math.Min(
                    (int)VoxelWorld.FluidCapacity,
                    oldFluidAmount + 1);
                if (newFluidAmount != oldFluidAmount)
                {
                    edits.Add(new VoxelEdit(
                        pending.X,
                        pending.Y,
                        pending.Z,
                        old,
                        pending.NewBlockId,
                        oldFluidAmount,
                        newFluidAmount,
                        true));
                }
                continue;
            }
            if (old == pending.NewBlockId && !hasMicro) continue;
            BlockDefinition oldDefinition = _blocks.Get(old);
            if (old != 0 && oldDefinition.IsUnbreakable && !oldDefinition.IsFluid) continue;
            if (hasMicro && micro!.Boxes.Any(box =>
                    _blocks.Get(box.MaterialId).IsUnbreakable && !_blocks.Get(box.MaterialId).IsFluid)) continue;
            if (pending.NewBlockId != 0 &&
                _wouldOverlapPlayer?.Invoke(pending.X, pending.Y, pending.Z) == true) continue;
            if (pending.NewBlockId != 0 && !newDefinition.IsPlaceable) continue;
            edits.Add(new VoxelEdit(
                pending.X,
                pending.Y,
                pending.Z,
                old,
                pending.NewBlockId,
                0,
                0,
                false));
        }

        if (edits.Count == 0)
        {
            Status = $"{name}: no editable voxels";
            return false;
        }

        _pendingEditCommands.Enqueue(new PendingEditCommand(name, edits));
        _queuedEditCount += edits.Count;
        if (AutoApplyQueuedEdits)
        {
            ApplyQueuedEdits();
        }
        Status = $"Queued {name}: {edits.Count:N0} voxels";
        return true;
    }

    private void PushUndo(VoxelEditCommand command)
    {
        _undo.Push(command);
        if (_undo.Count <= MaximumHistory) return;
        VoxelEditCommand[] keep = _undo.Take(MaximumHistory).Reverse().ToArray();
        _undo.Clear();
        foreach (VoxelEditCommand item in keep) _undo.Push(item);
    }

    private int ClipboardIndex(int x, int y, int z) =>
        x + _clipboardWidth * (y + _clipboardHeight * z);

    private void ClampOptions()
    {
        BrushRadius = Math.Clamp(BrushRadius, 1, 8);
        ExplosionRadius = Math.Clamp(ExplosionRadius, 1, 12);
    }

    private void ClearPendingEditQueue()
    {
        _pendingEditCommands.Clear();
        _queuedEditCount = 0;
    }

    private readonly record struct PendingEdit(int X, int Y, int Z, ushort NewBlockId);
    private readonly record struct VoxelEdit(
        int X,
        int Y,
        int Z,
        ushort OldBlockId,
        ushort NewBlockId,
        byte OldFluidAmount,
        byte NewFluidAmount,
        bool IsFluidEdit);
    private sealed record VoxelEditCommand(string Name, IReadOnlyList<VoxelEdit> Edits);

    private sealed class PendingEditCommand(string name, List<VoxelEdit> edits)
    {
        public string Name { get; } = name;
        public VoxelEdit[] Edits { get; } = edits.ToArray();
        public List<VoxelEdit> AppliedEdits { get; } = new();
        public int NextIndex { get; set; }
    }
}
