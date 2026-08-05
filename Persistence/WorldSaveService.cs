using System.Numerics;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RTXSandbox.ModAPI;
using RTXSandbox.Infrastructure;
using RTXSandbox.World;

namespace RTXSandbox.Persistence;

public sealed record WorldSaveSummary(
    string StorageName,
    string DisplayName,
    int Seed,
    WorldPreset Preset,
    DateTimeOffset LastSavedUtc,
    bool HasBackup);

public readonly struct SavedWorldState
{
    public Vector3 PlayerPosition { get; }
    public float TimeOfDay { get; }
    public int Seed { get; }
    public float Health { get; }
    public float Oxygen { get; }
    public float Hunger { get; }
    public int SelectedHotbarSlot { get; }
    public IReadOnlyList<ItemStack> Inventory { get; }
    public IReadOnlyList<string> CompletedObjectives { get; }
    public bool SawNight { get; }
    public WorldGenerationSettings GenerationSettings { get; }

    public SavedWorldState(
        Vector3 playerPosition,
        float timeOfDay,
        int seed = 1337,
        float health = 20f,
        float oxygen = 10f,
        float hunger = 20f,
        int selectedHotbarSlot = 0,
        IReadOnlyList<ItemStack>? inventory = null,
        IReadOnlyList<string>? completedObjectives = null,
        bool sawNight = false,
        WorldGenerationSettings? generationSettings = null)
    {
        PlayerPosition = playerPosition;
        TimeOfDay = timeOfDay;
        Seed = seed;
        Health = health;
        Oxygen = oxygen;
        Hunger = hunger;
        SelectedHotbarSlot = selectedHotbarSlot;
        Inventory = inventory ?? Array.Empty<ItemStack>();
        CompletedObjectives = completedObjectives ?? Array.Empty<string>();
        SawNight = sawNight;
        GenerationSettings = generationSettings?.Clone() ?? WorldGenerationSettings.Natural(seed);
    }
}

public sealed class WorldSaveService
{
    private const int Magic = 0x57585452; // RTXW in little endian
    private const int Version = 8;
    private const int ChunkMagic = 0x48435852; // RXCH
    private const int ChunkVersion = 1;
    private const int RegionMagic = 0x47525852; // RXRG
    private const int RegionVersion = 1;
    private const int PhysicsMagic = 0x48505852; // RXPH
    private const int PhysicsVersion = 1;

    private readonly BlockRegistry _blocks;
    private readonly bool _singleFileMode;
    private readonly string? _legacySavePath;
    private readonly string? _legacyRegionPath;

    public string SaveDirectory { get; }
    public string WorldsRoot { get; }
    public string SavePath { get; }
    public string BackupPath => SavePath + ".bak";
    public string TemporaryPath => SavePath + ".tmp";
    public string? LevelPath { get; }
    public string? PlayerPath { get; }
    public int WorldSeed { get; }
    public string WorldName { get; }
    public string DisplayName
    {
        get
        {
            if (_singleFileMode || LevelPath is null) return WorldName;
            WorldLevelMetadata? metadata = TryReadWorldMetadata(LevelPath) ??
                                           TryReadWorldMetadata(LevelPath + ".bak");
            return string.IsNullOrWhiteSpace(metadata?.Name) ? WorldName : metadata.Name;
        }
    }
    public string LastLoadSource { get; private set; } = "none";
    public bool RecoveredLastLoad { get; private set; }
    public double LastLoadMilliseconds { get; private set; }
    public double LastSaveMilliseconds { get; private set; }

    public WorldSaveService(
        BlockRegistry blocks,
        string? savePath = null,
        int preferredSeed = 1337,
        string worldName = "world1",
        string? saveRoot = null)
    {
        _blocks = blocks;
        _singleFileMode = !string.IsNullOrWhiteSpace(savePath);
        if (_singleFileMode)
        {
            SavePath = Path.GetFullPath(savePath!);
            SaveDirectory = Path.GetDirectoryName(SavePath) ?? AppContext.BaseDirectory;
            WorldsRoot = SaveDirectory;
            LevelPath = null;
            PlayerPath = null;
            WorldSeed = preferredSeed;
            WorldName = Path.GetFileNameWithoutExtension(SavePath);
            return;
        }

        WorldName = SanitizeWorldName(worldName);
        string worldsRoot = string.IsNullOrWhiteSpace(saveRoot)
            ? Path.Combine(RuntimePaths.OutputRoot, "Saves")
            : Path.GetFullPath(saveRoot);
        WorldsRoot = worldsRoot;
        SaveDirectory = Path.Combine(worldsRoot, WorldName);
        LevelPath = Path.Combine(SaveDirectory, "level.json");
        SavePath = LevelPath;
        PlayerPath = Path.Combine(SaveDirectory, "player.json");
        _legacyRegionPath = Path.Combine(SaveDirectory, "chunks", "0_0.chunk");
        _legacySavePath = string.Equals(WorldName, "world1", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(RuntimePaths.OutputRoot, "Saves", "world1.rtxw")
            : null;
        WorldSeed = ReadSeed(LevelPath, preferredSeed);
    }

    public IReadOnlyList<WorldSaveSummary> DiscoverWorlds()
    {
        if (_singleFileMode || !Directory.Exists(WorldsRoot)) return Array.Empty<WorldSaveSummary>();
        var worlds = new List<WorldSaveSummary>();
        try
        {
            foreach (string directory in Directory.GetDirectories(WorldsRoot))
            {
                string storageName = Path.GetFileName(directory);
                string levelPath = Path.Combine(directory, "level.json");
                string backupPath = levelPath + ".bak";
                WorldLevelMetadata? metadata = TryReadWorldMetadata(levelPath) ?? TryReadWorldMetadata(backupPath);
                if (metadata is null) continue;
                DateTimeOffset lastSaved = metadata.LastSavedUtc != default
                    ? metadata.LastSavedUtc
                    : new DateTimeOffset(File.GetLastWriteTimeUtc(File.Exists(levelPath) ? levelPath : backupPath));
                worlds.Add(new WorldSaveSummary(
                    storageName,
                    metadata.FormatVersion < 3 || string.IsNullOrWhiteSpace(metadata.Name)
                        ? storageName
                        : metadata.Name,
                    metadata.Seed,
                    metadata.Generation?.Preset ?? WorldPreset.Natural,
                    lastSaved,
                    File.Exists(backupPath)));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not enumerate worlds in '{WorldsRoot}': {exception.Message}");
        }
        return worlds
            .OrderByDescending(world => world.LastSavedUtc)
            .ThenBy(world => world.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public bool TryGetWorldGeneration(string storageName, out WorldGenerationSettings settings, out string? error)
    {
        settings = WorldGenerationSettings.Natural();
        error = null;
        try
        {
            string directory = ResolveManagedWorldDirectory(storageName);
            WorldLevelMetadata? metadata = TryReadWorldMetadata(Path.Combine(directory, "level.json")) ??
                                           TryReadWorldMetadata(Path.Combine(directory, "level.json.bak"));
            if (metadata is null) throw new InvalidDataException("World metadata is missing or invalid.");
            settings = metadata.Generation.Clone();
            settings.Seed = metadata.Seed;
            settings.Validate();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            error = exception.Message;
            return false;
        }
    }

    public bool TryRenameWorld(string storageName, string displayName, out string? error)
    {
        error = null;
        try
        {
            string directory = ResolveManagedWorldDirectory(storageName);
            string levelPath = Path.Combine(directory, "level.json");
            WorldLevelMetadata metadata = TryReadWorldMetadata(levelPath) ??
                throw new InvalidDataException("World metadata is missing or invalid.");
            string name = string.IsNullOrWhiteSpace(displayName) ? storageName : displayName.Trim();
            metadata.Name = name[..Math.Min(name.Length, 64)];
            metadata.LastSavedUtc = DateTimeOffset.UtcNow;
            WriteJsonAtomically(levelPath, metadata, keepBackup: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            error = exception.Message;
            return false;
        }
    }

    public bool TryDeleteWorld(string storageName, out string? error)
    {
        error = null;
        try
        {
            string directory = ResolveManagedWorldDirectory(storageName);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("World directory does not exist.");
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            error = exception.Message;
            return false;
        }
    }

    private string ResolveManagedWorldDirectory(string storageName)
    {
        if (_singleFileMode) throw new InvalidOperationException("World management requires directory saves.");
        string sanitized = SanitizeWorldName(storageName);
        if (!string.Equals(sanitized, storageName, StringComparison.Ordinal))
            throw new ArgumentException("Invalid world storage name.", nameof(storageName));
        string root = Path.GetFullPath(WorldsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string directory = Path.GetFullPath(Path.Combine(WorldsRoot, storageName));
        if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("World path escaped the Saves directory.");
        return directory;
    }

    private static WorldLevelMetadata? TryReadWorldMetadata(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            WorldLevelMetadata? metadata = JsonSerializer.Deserialize<WorldLevelMetadata>(
                File.ReadAllText(path), JsonOptions());
            return metadata is { FormatVersion: >= 1 and <= 4 } ? metadata : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string SanitizeWorldName(string worldName)
    {
        string value = string.IsNullOrWhiteSpace(worldName) ? "world1" : worldName.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        value = value.Trim('.', ' ');
        return string.IsNullOrWhiteSpace(value) ? "world1" : value[..Math.Min(value.Length, 64)];
    }

    public void Save(VoxelWorld world, Vector3 playerPosition, float timeOfDay)
        => Save(world, playerPosition, timeOfDay, inventory: null, health: 20f, oxygen: 10f);

    public void Save(
        VoxelWorld world,
        Vector3 playerPosition,
        float timeOfDay,
        IInventory? inventory,
        float health,
        float oxygen,
        float hunger = 20f,
        IReadOnlyList<string>? completedObjectives = null,
        bool sawNight = false)
    {
        long saveStart = Stopwatch.GetTimestamp();
        try
        {
            if (!_singleFileMode)
            {
                SaveChunkDirectory(
                    world, playerPosition, timeOfDay, inventory, health, oxygen, hunger,
                    completedObjectives, sawNight);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(SavePath) ?? SaveDirectory);
            WriteChunk(
                TemporaryPath,
                world,
                playerPosition,
                timeOfDay,
                inventory,
                health,
                oxygen,
                hunger,
                completedObjectives,
                sawNight);

            if (File.Exists(SavePath))
            {
                File.Copy(SavePath, BackupPath, overwrite: true);
            }
            File.Move(TemporaryPath, SavePath, overwrite: true);
        }
        finally
        {
            LastSaveMilliseconds = (Stopwatch.GetTimestamp() - saveStart) * 1000.0 / Stopwatch.Frequency;
        }
    }

    public bool TryLoad(VoxelWorld world, out SavedWorldState state, out string? error)
    {
        long loadStart = Stopwatch.GetTimestamp();
        try
        {
            state = default;
            error = null;
            LastLoadSource = "none";
            RecoveredLastLoad = false;

            string? directoryError = null;
            if (!_singleFileMode)
            {
                if (TryLoadChunkDirectory(world, out state, out directoryError)) return true;
                error = null;
            }

            var candidates = new List<(string Path, bool Recovery)>();
            if (_singleFileMode)
            {
                if (File.Exists(TemporaryPath)) candidates.Add((TemporaryPath, true));
                if (File.Exists(SavePath)) candidates.Add((SavePath, false));
                if (File.Exists(BackupPath)) candidates.Add((BackupPath, true));
            }
            if (!_singleFileMode && _legacyRegionPath is not null)
            {
                if (File.Exists(_legacyRegionPath + ".tmp")) candidates.Add((_legacyRegionPath + ".tmp", true));
                if (File.Exists(_legacyRegionPath)) candidates.Add((_legacyRegionPath, true));
                if (File.Exists(_legacyRegionPath + ".bak")) candidates.Add((_legacyRegionPath + ".bak", true));
            }
            if (!_singleFileMode && _legacySavePath is not null && File.Exists(_legacySavePath))
            {
                candidates.Add((_legacySavePath, true));
            }

            if (candidates.Count == 0)
            {
                error = directoryError;
                return false;
            }

            var failures = new List<string>();
            if (!string.IsNullOrWhiteSpace(directoryError)) failures.Add(directoryError);
            foreach ((string path, bool recovery) in candidates)
            {
                try
                {
                    state = ReadChunk(path, world);
                    LastLoadSource = path;
                    RecoveredLastLoad = recovery;
                    return true;
                }
                catch (Exception exception)
                {
                    failures.Add($"{Path.GetFileName(path)}: {exception.Message}");
                }
            }
            error = string.Join(" | ", failures);
            return false;
        }
        finally
        {
            LastLoadMilliseconds = (Stopwatch.GetTimestamp() - loadStart) * 1000.0 / Stopwatch.Frequency;
        }
    }

    private void SaveChunkDirectory(
        VoxelWorld world,
        Vector3 playerPosition,
        float timeOfDay,
        IInventory? inventory,
        float health,
        float oxygen,
        float hunger,
        IReadOnlyList<string>? completedObjectives,
        bool sawNight)
    {
        CapturedWorldSave capture = CaptureWorldSave(
            world,
            playerPosition,
            timeOfDay,
            inventory,
            health,
            oxygen,
            hunger,
            completedObjectives,
            sawNight);
        WriteCapturedChunkDirectory(capture);
    }

    public IncrementalSaveOperation BeginIncrementalSave(
        VoxelWorld world,
        Vector3 playerPosition,
        float timeOfDay,
        IInventory? inventory = null,
        float health = 20f,
        float oxygen = 10f,
        float hunger = 20f,
        IReadOnlyList<string>? completedObjectives = null,
        bool sawNight = false)
    {
        if (_singleFileMode)
            throw new InvalidOperationException("Incremental background save requires directory world storage.");
        return new IncrementalSaveOperation(
            this,
            world,
            playerPosition,
            timeOfDay,
            inventory,
            health,
            oxygen,
            hunger,
            completedObjectives,
            sawNight);
    }

    private CapturedWorldSave CaptureWorldSave(
        VoxelWorld world,
        Vector3 playerPosition,
        float timeOfDay,
        IInventory? inventory,
        float health,
        float oxygen,
        float hunger,
        IReadOnlyList<string>? completedObjectives,
        bool sawNight)
    {
        VoxelChunkSnapshot[] chunks = world.SnapshotGeneratedChunks()
            .OrderBy(chunk => chunk.Z)
            .ThenBy(chunk => chunk.X)
            .Select(world.SnapshotChunk)
            .ToArray();
        return CreateCapturedWorldSave(
            world,
            chunks,
            playerPosition,
            timeOfDay,
            inventory,
            health,
            oxygen,
            hunger,
            completedObjectives,
            sawNight);
    }

    private CapturedWorldSave CreateCapturedWorldSave(
        VoxelWorld world,
        IReadOnlyList<VoxelChunkSnapshot> chunks,
        Vector3 playerPosition,
        float timeOfDay,
        IInventory? inventory,
        float health,
        float oxygen,
        float hunger,
        IReadOnlyList<string>? completedObjectives,
        bool sawNight) =>
        new(
            world.Seed,
            world.GenerationSettings.Clone(),
            chunks,
            CapturePhysicsGrids(world.PhysicsGrids.Grids),
            CreatePlayerMetadata(
                playerPosition, inventory, health, oxygen, hunger, completedObjectives, sawNight),
            playerPosition,
            timeOfDay,
            _blocks.Definitions.Select(definition => definition.Key).ToArray(),
            BuildBlockKeyTable());

    private void WriteCapturedChunkDirectory(CapturedWorldSave capture)
    {
        if (LevelPath is null) throw new InvalidOperationException("Directory save has no level manifest path.");
        string generationId = $"g_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}";
        string relativeGenerationDirectory = $"regions/{generationId}";
        string generationDirectory = ResolveWorldRelativePath(relativeGenerationDirectory);
        Directory.CreateDirectory(generationDirectory);

        var regionFiles = new List<string>();
        foreach (IGrouping<RegionCoordinate, VoxelChunkSnapshot> region in capture.Chunks
                     .GroupBy(chunk => RegionCoordinate.FromChunk(chunk.Coordinate)))
        {
            string relativePath = $"{relativeGenerationDirectory}/{region.Key.X}_{region.Key.Z}.region";
            WriteRegion(
                ResolveWorldRelativePath(relativePath),
                region.Key,
                region.ToArray());
            regionFiles.Add(relativePath);
        }

        string physicsFile = $"{relativeGenerationDirectory}/physics.bin";
        WritePhysicsFile(ResolveWorldRelativePath(physicsFile), capture.PhysicsGrids);
        string playerFile = $"{relativeGenerationDirectory}/player.json";
        WritePlayerMetadata(ResolveWorldRelativePath(playerFile), capture.Player);

        string displayName = TryReadWorldMetadata(LevelPath)?.Name ?? WorldName;
        var metadata = new WorldLevelMetadata
        {
            FormatVersion = 4,
            // Folder/storage identity is intentionally separate from the user-facing
            // name. Autosave must not undo a rename.
            Name = string.IsNullOrWhiteSpace(displayName) ? WorldName : displayName,
            SaveGeneration = generationId,
            Seed = capture.Seed,
            LastSavedUtc = DateTimeOffset.UtcNow,
            Width = VoxelWorld.Width,
            Height = VoxelWorld.Height,
            Depth = VoxelWorld.Depth,
            ChunkSize = ChunkCoordinate.Size,
            RegionSizeChunks = RegionCoordinate.SizeInChunks,
            MinimumY = VoxelWorld.MinimumY,
            MaximumY = VoxelWorld.MaximumY,
            TimeOfDay = capture.TimeOfDay,
            PlayerPosition = new[] { capture.PlayerPosition.X, capture.PlayerPosition.Y, capture.PlayerPosition.Z },
            RequiredBlockKeys = capture.RequiredBlockKeys,
            BlockKeysById = capture.BlockKeysById,
            Regions = regionFiles.ToArray(),
            PhysicsFile = physicsFile,
            PlayerFile = playerFile,
            Generation = capture.Generation.Clone(),
        };
        // After recovery, preserve the known-good backup instead of replacing it with the
        // manifest that pointed at the corrupt generation.
        WriteJsonAtomically(LevelPath, metadata, keepBackup: !RecoveredLastLoad);
        RecoveredLastLoad = false;
    }

    private static void WriteRegion(
        string path,
        RegionCoordinate region,
        IReadOnlyList<VoxelChunkSnapshot> chunks)
    {
        WriteNewBinaryFile(path, writer =>
        {
            writer.Write(RegionMagic);
            writer.Write(RegionVersion);
            writer.Write(region.X);
            writer.Write(region.Z);
            writer.Write(ChunkCoordinate.Size);
            writer.Write(RegionCoordinate.SizeInChunks);
            writer.Write(VoxelWorld.MinimumY);
            writer.Write(VoxelWorld.MaximumY);
            writer.Write(chunks.Count);
            foreach (VoxelChunkSnapshot chunk in chunks)
            {
                writer.Write(chunk.Coordinate.X);
                writer.Write(chunk.Coordinate.Z);
                writer.Write(chunk.Voxels.Count);
                foreach (StoredVoxel voxel in chunk.Voxels)
                {
                    writer.Write((byte)voxel.LocalX);
                    writer.Write(voxel.Y);
                    writer.Write((byte)voxel.LocalZ);
                    writer.Write(voxel.MaterialId);
                }

                writer.Write(chunk.MicroVoxels.Count);
                foreach (StoredMicroVoxel micro in chunk.MicroVoxels)
                {
                    writer.Write((byte)micro.LocalX);
                    writer.Write(micro.Y);
                    writer.Write((byte)micro.LocalZ);
                    writer.Write(micro.Snapshot.Resolution);
                    writer.Write(micro.Snapshot.Boxes.Count);
                    foreach (MicroVoxelBox box in micro.Snapshot.Boxes)
                    {
                        writer.Write(box.MinX); writer.Write(box.MinY); writer.Write(box.MinZ);
                        writer.Write(box.MaxX); writer.Write(box.MaxY); writer.Write(box.MaxZ);
                        writer.Write(box.MaterialId);
                    }
                }
            }
        });
    }

    private static IReadOnlyList<SavedPhysicsGrid> CapturePhysicsGrids(IReadOnlyList<PhysicsGrid> grids) =>
        grids.Select(grid => new SavedPhysicsGrid(
            grid.Position,
            grid.Orientation,
            grid.LinearVelocity,
            grid.AngularVelocity,
            grid.IsSleeping,
            grid.FractureOnImpact,
            grid.Elements.ToArray())).ToArray();

    private void WritePhysicsFile(string path, IReadOnlyList<SavedPhysicsGrid> grids)
    {
        WriteNewBinaryFile(path, writer =>
        {
            writer.Write(PhysicsMagic);
            writer.Write(PhysicsVersion);
            writer.Write(grids.Count);
            foreach (SavedPhysicsGrid grid in grids)
            {
                WriteVector3(writer, grid.Position);
                writer.Write(grid.Orientation.X); writer.Write(grid.Orientation.Y);
                writer.Write(grid.Orientation.Z); writer.Write(grid.Orientation.W);
                WriteVector3(writer, grid.LinearVelocity);
                WriteVector3(writer, grid.AngularVelocity);
                writer.Write(grid.Sleeping);
                writer.Write(grid.FractureOnImpact);
                writer.Write(grid.Elements.Count);
                foreach (PhysicsGridElement element in grid.Elements)
                {
                    writer.Write(element.CellX); writer.Write(element.CellY); writer.Write(element.CellZ);
                    WriteVector3(writer, element.Minimum);
                    WriteVector3(writer, element.Maximum);
                    writer.Write(element.MaterialId);
                }
            }
        });
    }

    private string[] BuildBlockKeyTable()
    {
        int length = _blocks.Definitions.Count == 0
            ? 1
            : _blocks.Definitions.Max(definition => (int)definition.Id) + 1;
        var keys = new string[length];
        Array.Fill(keys, string.Empty);
        foreach (BlockDefinition definition in _blocks.Definitions) keys[definition.Id] = definition.Key;
        return keys;
    }

    private static void WriteNewBinaryFile(string path, Action<BinaryWriter> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("File has no directory."));
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        write(writer);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private void WriteJsonAtomically<T>(string path, T value, bool keepBackup)
    {
        string temporaryPath = path + ".tmp";
        string backupPath = path + ".bak";
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? SaveDirectory);
        string json = JsonSerializer.Serialize(value, JsonOptions());
        using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true);
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        if (!File.Exists(path))
        {
            File.Move(temporaryPath, path);
        }
        else if (keepBackup)
        {
            File.Replace(temporaryPath, path, backupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporaryPath, path, overwrite: true);
        }
    }

    private string ResolveWorldRelativePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) throw new InvalidDataException("World manifest path must be relative.");
        string root = Path.GetFullPath(SaveDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                      Path.DirectorySeparatorChar;
        string combined = Path.GetFullPath(Path.Combine(
            SaveDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("World manifest path escapes the save directory.");
        return combined;
    }

    private bool TryLoadChunkDirectory(VoxelWorld world, out SavedWorldState state, out string? error)
    {
        state = default;
        error = null;
        if (LevelPath is null) return false;
        var manifests = new List<(string Path, bool Recovery)>();
        if (File.Exists(LevelPath + ".tmp")) manifests.Add((LevelPath + ".tmp", true));
        if (File.Exists(LevelPath)) manifests.Add((LevelPath, false));
        if (File.Exists(LevelPath + ".bak")) manifests.Add((LevelPath + ".bak", true));
        if (manifests.Count == 0) return false;

        var failures = new List<string>();
        foreach ((string manifestPath, bool recovery) in manifests)
        {
            try
            {
                WorldLevelMetadata metadata = JsonSerializer.Deserialize<WorldLevelMetadata>(
                                                  File.ReadAllText(manifestPath), JsonOptions())
                                              ?? throw new InvalidDataException("World manifest is empty.");
                if (metadata.FormatVersion >= 3)
                {
                    state = ReadChunkDirectory(metadata, world);
                }
                else
                {
                    string legacyRelativePath = metadata.Chunks.FirstOrDefault()
                                                ?? throw new InvalidDataException("Legacy manifest has no region file.");
                    state = ReadChunk(ResolveWorldRelativePath(legacyRelativePath), world);
                }
                LastLoadSource = manifestPath;
                RecoveredLastLoad = recovery;
                return true;
            }
            catch (Exception exception)
            {
                failures.Add($"{Path.GetFileName(manifestPath)}: {exception.Message}");
            }
        }
        error = string.Join(" | ", failures);
        return false;
    }

    private SavedWorldState ReadChunkDirectory(WorldLevelMetadata metadata, VoxelWorld world)
    {
        if (metadata.FormatVersion == 4) return ReadRegionDirectory(metadata, world);
        if (metadata.FormatVersion != 3)
            throw new InvalidDataException($"Unsupported directory world format {metadata.FormatVersion}.");
        if (metadata.Width != VoxelWorld.Width || metadata.Height <= 0 ||
            metadata.Height > VoxelWorld.MaximumY ||
            metadata.Depth != VoxelWorld.Depth || metadata.ChunkSize != ChunkCoordinate.Size)
            throw new InvalidDataException(
                $"World dimensions {metadata.Width}x{metadata.Height}x{metadata.Depth} or chunk size {metadata.ChunkSize} do not match this build.");
        if (metadata.Chunks.Length > 65_536) throw new InvalidDataException("World manifest contains too many chunks.");

        ushort[] remap = BuildDirectoryMaterialRemap(metadata.BlockKeysById);
        var voxels = new ushort[VoxelWorld.Width * VoxelWorld.Height * VoxelWorld.Depth];
        var microVoxels = new List<(int X, int Y, int Z, MicroVoxelSnapshot Snapshot)>();
        var coordinates = new HashSet<ChunkCoordinate>();
        foreach (string relativePath in metadata.Chunks)
        {
            ChunkCoordinate coordinate = ReadSparseChunk(
                ResolveWorldRelativePath(relativePath), remap, voxels, microVoxels);
            if (!coordinates.Add(coordinate)) throw new InvalidDataException($"Duplicate chunk {coordinate} in manifest.");
        }

        string physicsPath = string.IsNullOrWhiteSpace(metadata.PhysicsFile)
            ? throw new InvalidDataException("World manifest has no physics state file.")
            : ResolveWorldRelativePath(metadata.PhysicsFile);
        IReadOnlyList<SavedPhysicsGrid> physicsGrids = ReadPhysicsFile(physicsPath, remap);
        PlayerMetadata player = string.IsNullOrWhiteSpace(metadata.PlayerFile)
            ? throw new InvalidDataException("World manifest has no player state file.")
            : ReadPlayerMetadata(ResolveWorldRelativePath(metadata.PlayerFile));

        world.LoadRawData(voxels);
        world.LoadMicroVoxels(microVoxels);
        RestorePhysicsGrids(world, physicsGrids);
        WorldGenerationSettings generation = metadata.Generation?.Clone()
                                             ?? WorldGenerationSettings.Natural(metadata.Seed);
        generation.Seed = metadata.Seed;
        generation.Validate();
        world.SetGenerationMetadata(generation);

        Vector3 fallbackPosition = metadata.PlayerPosition.Length == 3
            ? new Vector3(metadata.PlayerPosition[0], metadata.PlayerPosition[1], metadata.PlayerPosition[2])
            : Vector3.Zero;
        Vector3 playerPosition = player.Position.Length == 3
            ? new Vector3(player.Position[0], player.Position[1], player.Position[2])
            : fallbackPosition;
        IReadOnlyList<ItemStack> inventory = RestorePlayerInventory(player);
        return new SavedWorldState(
            playerPosition,
            metadata.TimeOfDay,
            metadata.Seed,
            player.Health,
            player.Oxygen,
            player.Hunger,
            player.SelectedHotbarSlot,
            inventory,
            player.CompletedObjectives,
            player.SawNight,
            generation);
    }

    private SavedWorldState ReadRegionDirectory(WorldLevelMetadata metadata, VoxelWorld world)
    {
        if (metadata.ChunkSize != ChunkCoordinate.Size ||
            metadata.RegionSizeChunks != RegionCoordinate.SizeInChunks ||
            metadata.MinimumY != VoxelWorld.MinimumY || metadata.MaximumY != VoxelWorld.MaximumY)
            throw new InvalidDataException("World region dimensions do not match this build.");
        if (metadata.Regions.Length > 65_536)
            throw new InvalidDataException("World manifest contains too many regions.");

        ushort[] remap = BuildDirectoryMaterialRemap(metadata.BlockKeysById);
        var chunks = new List<VoxelChunkSnapshot>();
        var coordinates = new HashSet<ChunkCoordinate>();
        foreach (string relativePath in metadata.Regions)
        {
            foreach (VoxelChunkSnapshot chunk in ReadRegion(ResolveWorldRelativePath(relativePath), remap))
            {
                if (!coordinates.Add(chunk.Coordinate))
                    throw new InvalidDataException($"Duplicate chunk {chunk.Coordinate} in region manifest.");
                chunks.Add(chunk);
            }
        }

        string physicsPath = string.IsNullOrWhiteSpace(metadata.PhysicsFile)
            ? throw new InvalidDataException("World manifest has no physics state file.")
            : ResolveWorldRelativePath(metadata.PhysicsFile);
        IReadOnlyList<SavedPhysicsGrid> physicsGrids = ReadPhysicsFile(physicsPath, remap);
        PlayerMetadata player = string.IsNullOrWhiteSpace(metadata.PlayerFile)
            ? throw new InvalidDataException("World manifest has no player state file.")
            : ReadPlayerMetadata(ResolveWorldRelativePath(metadata.PlayerFile));

        world.LoadChunks(chunks);
        RestorePhysicsGrids(world, physicsGrids);
        WorldGenerationSettings generation = metadata.Generation?.Clone()
                                             ?? WorldGenerationSettings.Natural(metadata.Seed);
        generation.Seed = metadata.Seed;
        generation.Validate();
        world.SetGenerationMetadata(generation);

        Vector3 fallbackPosition = metadata.PlayerPosition.Length == 3
            ? new Vector3(metadata.PlayerPosition[0], metadata.PlayerPosition[1], metadata.PlayerPosition[2])
            : Vector3.Zero;
        Vector3 playerPosition = player.Position.Length == 3
            ? new Vector3(player.Position[0], player.Position[1], player.Position[2])
            : fallbackPosition;
        return new SavedWorldState(
            playerPosition,
            metadata.TimeOfDay,
            metadata.Seed,
            player.Health,
            player.Oxygen,
            player.Hunger,
            player.SelectedHotbarSlot,
            RestorePlayerInventory(player),
            player.CompletedObjectives,
            player.SawNight,
            generation);
    }

    private static IReadOnlyList<VoxelChunkSnapshot> ReadRegion(string path, ushort[] remap)
    {
        using FileStream stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (reader.ReadInt32() != RegionMagic) throw new InvalidDataException("Invalid region header.");
        int version = reader.ReadInt32();
        if (version != RegionVersion) throw new InvalidDataException($"Unsupported region version {version}.");
        var region = new RegionCoordinate(reader.ReadInt32(), reader.ReadInt32());
        int chunkSize = reader.ReadInt32();
        int regionSize = reader.ReadInt32();
        int minimumY = reader.ReadInt32();
        int maximumY = reader.ReadInt32();
        if (chunkSize != ChunkCoordinate.Size || regionSize != RegionCoordinate.SizeInChunks ||
            minimumY != VoxelWorld.MinimumY || maximumY != VoxelWorld.MaximumY)
            throw new InvalidDataException("Region dimensions do not match this build.");

        int chunkCount = reader.ReadInt32();
        if (chunkCount < 0 || chunkCount > RegionCoordinate.SizeInChunks * RegionCoordinate.SizeInChunks)
            throw new InvalidDataException("Invalid chunk count in region.");
        int maximumCells = ChunkCoordinate.Size * VoxelWorld.Height * ChunkCoordinate.Size;
        var chunks = new List<VoxelChunkSnapshot>(chunkCount);
        var chunkCoordinates = new HashSet<ChunkCoordinate>();
        for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            var coordinate = new ChunkCoordinate(reader.ReadInt32(), reader.ReadInt32());
            if (!region.Contains(coordinate) || !chunkCoordinates.Add(coordinate))
                throw new InvalidDataException("Region contains an invalid or duplicate chunk coordinate.");

            int regularCount = reader.ReadInt32();
            if (regularCount < 0 || regularCount > maximumCells)
                throw new InvalidDataException("Invalid regular voxel count in region chunk.");
            var occupied = new HashSet<(byte X, int Y, byte Z)>();
            var voxels = new List<StoredVoxel>(regularCount);
            for (int voxelIndex = 0; voxelIndex < regularCount; voxelIndex++)
            {
                byte localX = reader.ReadByte();
                int y = reader.ReadInt32();
                byte localZ = reader.ReadByte();
                ushort savedMaterial = reader.ReadUInt16();
                if (localX >= ChunkCoordinate.Size || localZ >= ChunkCoordinate.Size ||
                    y < VoxelWorld.MinimumY || y >= VoxelWorld.MaximumY ||
                    savedMaterial >= remap.Length || !occupied.Add((localX, y, localZ)))
                    throw new InvalidDataException("Region contains an invalid regular voxel.");
                ushort material = remap[savedMaterial];
                if (material != 0) voxels.Add(new StoredVoxel(localX, y, localZ, material));
            }

            int microCount = reader.ReadInt32();
            if (microCount < 0 || microCount > maximumCells)
                throw new InvalidDataException("Invalid precision cell count in region chunk.");
            var microVoxels = new List<StoredMicroVoxel>(microCount);
            for (int microIndex = 0; microIndex < microCount; microIndex++)
            {
                byte localX = reader.ReadByte();
                int y = reader.ReadInt32();
                byte localZ = reader.ReadByte();
                int resolution = reader.ReadInt32();
                int boxCount = reader.ReadInt32();
                if (localX >= ChunkCoordinate.Size || localZ >= ChunkCoordinate.Size ||
                    y < VoxelWorld.MinimumY || y >= VoxelWorld.MaximumY ||
                    !occupied.Add((localX, y, localZ)) ||
                    resolution < 1 || resolution > MicroVoxelContainer.MaximumResolution ||
                    boxCount < 0 || boxCount > MicroVoxelContainer.MaximumBoxes)
                    throw new InvalidDataException("Region contains invalid precision geometry.");
                var boxes = new List<MicroVoxelBox>(boxCount);
                for (int boxIndex = 0; boxIndex < boxCount; boxIndex++)
                {
                    int minX = reader.ReadInt32(); int minY = reader.ReadInt32(); int minZ = reader.ReadInt32();
                    int maxX = reader.ReadInt32(); int maxY = reader.ReadInt32(); int maxZ = reader.ReadInt32();
                    ushort savedMaterial = reader.ReadUInt16();
                    if (savedMaterial >= remap.Length)
                        throw new InvalidDataException("Precision material exceeds the saved material table.");
                    ushort material = remap[savedMaterial];
                    if (material != 0)
                        boxes.Add(new MicroVoxelBox(minX, minY, minZ, maxX, maxY, maxZ, material));
                }
                if (boxes.Count > 0)
                {
                    var snapshot = new MicroVoxelSnapshot(resolution, boxes);
                    _ = new MicroVoxelContainer(snapshot);
                    microVoxels.Add(new StoredMicroVoxel(localX, y, localZ, snapshot));
                }
            }
            chunks.Add(new VoxelChunkSnapshot(coordinate, voxels, microVoxels));
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Region has trailing data.");
        return chunks;
    }

    private ChunkCoordinate ReadSparseChunk(
        string path,
        ushort[] remap,
        ushort[] voxels,
        List<(int X, int Y, int Z, MicroVoxelSnapshot Snapshot)> microVoxels)
    {
        using FileStream stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (reader.ReadInt32() != ChunkMagic) throw new InvalidDataException("Invalid sparse chunk header.");
        int version = reader.ReadInt32();
        if (version != ChunkVersion) throw new InvalidDataException($"Unsupported sparse chunk version {version}.");
        var coordinate = new ChunkCoordinate(reader.ReadInt32(), reader.ReadInt32());
        int chunkSize = reader.ReadInt32();
        int height = reader.ReadInt32();
        if (chunkSize != ChunkCoordinate.Size || height <= 0 || height > VoxelWorld.MaximumY)
            throw new InvalidDataException("Sparse chunk dimensions do not match this build.");
        if (coordinate.MinimumX < 0 || coordinate.MinimumZ < 0 ||
            coordinate.MinimumX >= VoxelWorld.Width || coordinate.MinimumZ >= VoxelWorld.Depth)
            throw new InvalidDataException($"Chunk {coordinate} is outside the current world bounds.");

        int maximumCells = ChunkCoordinate.Size * height * ChunkCoordinate.Size;
        int regularCount = reader.ReadInt32();
        if (regularCount < 0 || regularCount > maximumCells)
            throw new InvalidDataException("Invalid regular voxel count in sparse chunk.");
        var occupiedIndices = new HashSet<int>();
        for (int entry = 0; entry < regularCount; entry++)
        {
            int localIndex = reader.ReadInt32();
            ushort savedMaterial = reader.ReadUInt16();
            if (localIndex < 0 || localIndex >= maximumCells || !occupiedIndices.Add(localIndex))
                throw new InvalidDataException("Sparse chunk contains an invalid or duplicate regular voxel index.");
            if (savedMaterial >= remap.Length)
                throw new InvalidDataException("Sparse chunk material exceeds the saved material table.");
            DecodeLocalChunkIndex(localIndex, height, out int localX, out int y, out int localZ);
            int x = coordinate.MinimumX + localX;
            int z = coordinate.MinimumZ + localZ;
            if (x >= VoxelWorld.Width || z >= VoxelWorld.Depth)
                throw new InvalidDataException("Sparse chunk voxel is outside the world edge.");
            ushort material = remap[savedMaterial];
            if (material != 0) voxels[WorldLinearIndex(x, y - VoxelWorld.MinimumY, z)] = material;
        }

        int microCount = reader.ReadInt32();
        if (microCount < 0 || microCount > maximumCells)
            throw new InvalidDataException("Invalid precision cell count in sparse chunk.");
        var microIndices = new HashSet<(int X, int Y, int Z)>();
        for (int cell = 0; cell < microCount; cell++)
        {
            int localX = reader.ReadInt32();
            int y = reader.ReadInt32();
            int localZ = reader.ReadInt32();
            int resolution = reader.ReadInt32();
            int boxCount = reader.ReadInt32();
            if (localX < 0 || localX >= ChunkCoordinate.Size || localZ < 0 || localZ >= ChunkCoordinate.Size ||
                y < VoxelWorld.MinimumY || y >= VoxelWorld.MaximumY)
                throw new InvalidDataException("Precision cell is outside its chunk.");
            int regularLocalIndex = localX + ChunkCoordinate.Size * (y + height * localZ);
            if (!microIndices.Add((localX, y, localZ)) ||
                (y >= 0 && y < height && occupiedIndices.Contains(regularLocalIndex)))
                throw new InvalidDataException("Sparse chunk contains overlapping or duplicate precision geometry.");
            if (resolution < 1 || resolution > MicroVoxelContainer.MaximumResolution ||
                boxCount < 0 || boxCount > MicroVoxelContainer.MaximumBoxes)
                throw new InvalidDataException("Invalid precision geometry dimensions.");

            var boxes = new List<MicroVoxelBox>(boxCount);
            for (int box = 0; box < boxCount; box++)
            {
                int minX = reader.ReadInt32(); int minY = reader.ReadInt32(); int minZ = reader.ReadInt32();
                int maxX = reader.ReadInt32(); int maxY = reader.ReadInt32(); int maxZ = reader.ReadInt32();
                ushort savedMaterial = reader.ReadUInt16();
                if (savedMaterial >= remap.Length)
                    throw new InvalidDataException("Precision material exceeds the saved material table.");
                ushort material = remap[savedMaterial];
                if (material != 0)
                    boxes.Add(new MicroVoxelBox(minX, minY, minZ, maxX, maxY, maxZ, material));
            }
            if (boxes.Count == 0) continue;
            var snapshot = new MicroVoxelSnapshot(resolution, boxes);
            _ = new MicroVoxelContainer(snapshot);
            int worldX = coordinate.MinimumX + localX;
            int worldZ = coordinate.MinimumZ + localZ;
            if (worldX >= VoxelWorld.Width || worldZ >= VoxelWorld.Depth)
                throw new InvalidDataException("Precision cell is outside the world edge.");
            microVoxels.Add((worldX, y, worldZ, snapshot));
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Sparse chunk has trailing data.");
        return coordinate;
    }

    private IReadOnlyList<SavedPhysicsGrid> ReadPhysicsFile(string path, ushort[] remap)
    {
        using FileStream stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (reader.ReadInt32() != PhysicsMagic) throw new InvalidDataException("Invalid physics state header.");
        int version = reader.ReadInt32();
        if (version != PhysicsVersion) throw new InvalidDataException($"Unsupported physics state version {version}.");
        int gridCount = reader.ReadInt32();
        if (gridCount < 0 || gridCount > 4096) throw new InvalidDataException("Invalid physics grid count.");
        var grids = new List<SavedPhysicsGrid>(gridCount);
        for (int gridIndex = 0; gridIndex < gridCount; gridIndex++)
        {
            Vector3 position = ReadVector3(reader);
            var orientation = new Quaternion(
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (orientation.LengthSquared() < 0.5f) throw new InvalidDataException("Invalid physics grid orientation.");
            orientation = Quaternion.Normalize(orientation);
            Vector3 linearVelocity = ReadVector3(reader);
            Vector3 angularVelocity = ReadVector3(reader);
            bool sleeping = reader.ReadBoolean();
            bool fractureOnImpact = reader.ReadBoolean();
            int elementCount = reader.ReadInt32();
            if (elementCount <= 0 || elementCount > 1_000_000)
                throw new InvalidDataException("Invalid physics grid element count.");
            var elements = new PhysicsGridElement[elementCount];
            for (int elementIndex = 0; elementIndex < elementCount; elementIndex++)
            {
                int cellX = reader.ReadInt32(); int cellY = reader.ReadInt32(); int cellZ = reader.ReadInt32();
                Vector3 minimum = ReadVector3(reader);
                Vector3 maximum = ReadVector3(reader);
                ushort savedMaterial = reader.ReadUInt16();
                if (savedMaterial >= remap.Length)
                    throw new InvalidDataException("Physics grid material exceeds the saved material table.");
                ushort material = remap[savedMaterial];
                if (material == 0) material = BlockRegistry.UnknownBlockId;
                elements[elementIndex] = new PhysicsGridElement(
                    cellX, cellY, cellZ, minimum, maximum, material);
            }
            grids.Add(new SavedPhysicsGrid(
                position, orientation, linearVelocity, angularVelocity, sleeping, fractureOnImpact, elements));
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Physics state has trailing data.");
        return grids;
    }

    private static PlayerMetadata ReadPlayerMetadata(string path)
    {
        PlayerMetadata metadata = JsonSerializer.Deserialize<PlayerMetadata>(File.ReadAllText(path), JsonOptions())
                                  ?? throw new InvalidDataException("Player state is empty.");
        if (metadata.FormatVersion != 1) throw new InvalidDataException("Unsupported player state version.");
        if (metadata.InventorySlotCount < 0 || metadata.InventorySlotCount > 256 ||
            metadata.Inventory.Length > 256)
            throw new InvalidDataException("Invalid player inventory size.");
        return metadata;
    }

    private static IReadOnlyList<ItemStack> RestorePlayerInventory(PlayerMetadata player)
    {
        int inferredSlots = player.Inventory.Length == 0 ? 0 : player.Inventory.Max(item => item.Slot) + 1;
        int slotCount = Math.Max(player.InventorySlotCount, inferredSlots);
        if (slotCount < 0 || slotCount > 256) throw new InvalidDataException("Invalid player inventory slot.");
        var inventory = new ItemStack[slotCount];
        foreach (PlayerItemMetadata item in player.Inventory)
        {
            if (item.Slot < 0 || item.Slot >= slotCount || item.Count < 0)
                throw new InvalidDataException("Invalid player inventory entry.");
            inventory[item.Slot] = new ItemStack(item.ItemKey, item.Count, item.Damage);
        }
        return inventory;
    }

    private ushort[] BuildDirectoryMaterialRemap(string[] keysById)
    {
        if (keysById.Length == 0 || keysById.Length > BlockRegistry.MaterialCapacity)
            throw new InvalidDataException("World manifest has an invalid material table.");
        var remap = new ushort[keysById.Length];
        for (int id = 0; id < keysById.Length; id++)
        {
            string key = keysById[id];
            if (!string.IsNullOrWhiteSpace(key) && _blocks.TryGet(key, out BlockDefinition? definition))
                remap[id] = definition!.Id;
        }
        return remap;
    }

    private static void RestorePhysicsGrids(VoxelWorld world, IReadOnlyList<SavedPhysicsGrid> physicsGrids)
    {
        foreach (SavedPhysicsGrid savedGrid in physicsGrids)
        {
            PhysicsGrid grid = world.PhysicsGrids.Create(savedGrid.Elements, Vector3.Zero);
            grid.SetTransformAndVelocity(
                savedGrid.Position, savedGrid.Orientation, savedGrid.LinearVelocity, savedGrid.AngularVelocity);
            grid.IsSleeping = savedGrid.Sleeping;
            grid.FractureOnImpact = savedGrid.FractureOnImpact;
        }
    }

    private static void DecodeLocalChunkIndex(
        int localIndex,
        int height,
        out int localX,
        out int y,
        out int localZ)
    {
        localX = localIndex % ChunkCoordinate.Size;
        int yz = localIndex / ChunkCoordinate.Size;
        y = yz % height;
        localZ = yz / height;
    }

    private static int WorldLinearIndex(int x, int y, int z) =>
        x + VoxelWorld.Width * (y + VoxelWorld.Height * z);

    private void WriteChunk(
        string path,
        VoxelWorld world,
        Vector3 playerPosition,
        float timeOfDay,
        IInventory? inventory,
        float health,
        float oxygen,
        float hunger,
        IReadOnlyList<string>? completedObjectives,
        bool sawNight)
    {
        using FileStream stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(VoxelWorld.Width);
        writer.Write(VoxelWorld.Height);
        writer.Write(VoxelWorld.Depth);
        writer.Write(VoxelWorld.MinimumY);
        writer.Write(VoxelWorld.MaximumY);
        writer.Write(timeOfDay);
        writer.Write(playerPosition.X);
        writer.Write(playerPosition.Y);
        writer.Write(playerPosition.Z);
        writer.Write(health);
        writer.Write(oxygen);
        writer.Write(hunger);
        writer.Write(inventory?.SelectedHotbarSlot ?? 0);
        writer.Write(inventory?.SlotCount ?? 0);
        if (inventory is not null)
        {
            for (int slot = 0; slot < inventory.SlotCount; slot++)
            {
                ItemStack stack = inventory.GetSlot(slot);
                writer.Write(stack.ItemKey ?? string.Empty);
                writer.Write(stack.Count);
                writer.Write(stack.Damage);
            }
        }

        writer.Write(completedObjectives?.Count ?? 0);
        if (completedObjectives is not null)
        {
            foreach (string objectiveId in completedObjectives)
            {
                writer.Write(objectiveId);
            }
        }
        writer.Write(sawNight);
        WorldGenerationSettings generation = world.GenerationSettings;
        writer.Write(generation.Preset.ToString());
        writer.Write(generation.Biome.ToString());
        writer.Write(generation.Seed);
        writer.Write(generation.FlatSurfaceHeight);
        writer.Write(generation.SurfaceBlockKey);
        writer.Write(generation.UndergroundBlockKey);
        writer.Write(generation.GenerateLakes);
        writer.Write(generation.GenerateVegetation);

        IReadOnlyList<BlockDefinition> definitions = _blocks.Definitions;
        writer.Write(definitions.Count);
        foreach (BlockDefinition definition in definitions)
        {
            writer.Write(definition.Id);
            writer.Write(definition.Key);
        }

        ushort[] rawData = world.RawData;
        writer.Write(rawData.Length);
        foreach (ushort blockId in rawData)
        {
            writer.Write(blockId);
        }

        IReadOnlyList<(int X, int Y, int Z, MicroVoxelSnapshot Snapshot)> microCells = world.SnapshotMicroVoxels();
        writer.Write(microCells.Count);
        foreach ((int x, int y, int z, MicroVoxelSnapshot snapshot) in microCells)
        {
            writer.Write(x);
            writer.Write(y);
            writer.Write(z);
            writer.Write(snapshot.Resolution);
            writer.Write(snapshot.Boxes.Count);
            foreach (MicroVoxelBox box in snapshot.Boxes)
            {
                writer.Write(box.MinX);
                writer.Write(box.MinY);
                writer.Write(box.MinZ);
                writer.Write(box.MaxX);
                writer.Write(box.MaxY);
                writer.Write(box.MaxZ);
                writer.Write(box.MaterialId);
            }
        }

        writer.Write(world.PhysicsGrids.Count);
        foreach (PhysicsGrid grid in world.PhysicsGrids.Grids)
        {
            WriteVector3(writer, grid.Position);
            writer.Write(grid.Orientation.X);
            writer.Write(grid.Orientation.Y);
            writer.Write(grid.Orientation.Z);
            writer.Write(grid.Orientation.W);
            WriteVector3(writer, grid.LinearVelocity);
            WriteVector3(writer, grid.AngularVelocity);
            writer.Write(grid.IsSleeping);
            writer.Write(grid.FractureOnImpact);
            writer.Write(grid.Elements.Count);
            foreach (PhysicsGridElement element in grid.Elements)
            {
                writer.Write(element.CellX);
                writer.Write(element.CellY);
                writer.Write(element.CellZ);
                WriteVector3(writer, element.Minimum);
                WriteVector3(writer, element.Maximum);
                writer.Write(element.MaterialId);
            }
        }
        stream.Flush(flushToDisk: true);
    }

    private SavedWorldState ReadChunk(string path, VoxelWorld world)
    {
        using FileStream stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (reader.ReadInt32() != Magic)
        {
            throw new InvalidDataException("Not an RTX Sandbox world file.");
        }
        int version = reader.ReadInt32();
        if (version < 1 || version > Version)
        {
            throw new InvalidDataException($"Unsupported world version {version}; expected 1-{Version}.");
        }
        int width = reader.ReadInt32();
        int height = reader.ReadInt32();
        int depth = reader.ReadInt32();
        int sourceMinimumY = version >= 8 ? reader.ReadInt32() : 0;
        int sourceMaximumY = version >= 8 ? reader.ReadInt32() : checked(sourceMinimumY + height);
        if (width != VoxelWorld.Width || depth != VoxelWorld.Depth || height <= 0 ||
            sourceMaximumY - sourceMinimumY != height ||
            sourceMinimumY < VoxelWorld.MinimumY || sourceMaximumY > VoxelWorld.MaximumY)
        {
            throw new InvalidDataException($"World dimensions {width}x{height}x{depth} do not match this build.");
        }

        float time = reader.ReadSingle();
        var playerPosition = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        float health = 20f;
        float oxygen = 10f;
        float hunger = 20f;
        int selectedHotbarSlot = 0;
        IReadOnlyList<ItemStack> inventory = Array.Empty<ItemStack>();
        IReadOnlyList<string> completedObjectives = Array.Empty<string>();
        bool sawNight = false;
        WorldGenerationSettings generation = WorldGenerationSettings.Natural(WorldSeed);
        if (version >= 2)
        {
            health = reader.ReadSingle();
            oxygen = reader.ReadSingle();
            if (version >= 3)
            {
                hunger = reader.ReadSingle();
            }
            selectedHotbarSlot = reader.ReadInt32();
            int inventorySlots = reader.ReadInt32();
            if (inventorySlots < 0 || inventorySlots > 256)
            {
                throw new InvalidDataException("Invalid inventory slot count.");
            }
            var stacks = new ItemStack[inventorySlots];
            for (int slot = 0; slot < inventorySlots; slot++)
            {
                stacks[slot] = new ItemStack(reader.ReadString(), reader.ReadInt32(), reader.ReadInt32());
            }
            inventory = stacks;
            if (version >= 3)
            {
                int objectiveCount = reader.ReadInt32();
                if (objectiveCount < 0 || objectiveCount > 1024)
                {
                    throw new InvalidDataException("Invalid completed objective count.");
                }
                var objectives = new string[objectiveCount];
                for (int objective = 0; objective < objectiveCount; objective++)
                {
                    objectives[objective] = reader.ReadString();
                }
                completedObjectives = objectives;
                sawNight = reader.ReadBoolean();
            }
        }
        if (version >= 4)
        {
            if (Enum.TryParse(reader.ReadString(), ignoreCase: true, out WorldPreset preset)) generation.Preset = preset;
            if (Enum.TryParse(reader.ReadString(), ignoreCase: true, out BiomePreset biome)) generation.Biome = biome;
            generation.Seed = reader.ReadInt32();
            generation.FlatSurfaceHeight = reader.ReadInt32();
            generation.SurfaceBlockKey = reader.ReadString();
            generation.UndergroundBlockKey = reader.ReadString();
            generation.GenerateLakes = reader.ReadBoolean();
            generation.GenerateVegetation = reader.ReadBoolean();
            generation.Validate();
        }
        var remap = new ushort[BlockRegistry.MaterialCapacity];
        int definitionCount = reader.ReadInt32();
        if (definitionCount < 0 || definitionCount > BlockRegistry.MaterialCapacity)
        {
            throw new InvalidDataException("Invalid block definition count.");
        }
        for (int i = 0; i < definitionCount; i++)
        {
            ushort savedId = version >= 5 ? reader.ReadUInt16() : reader.ReadByte();
            string key = reader.ReadString();
            if (savedId >= remap.Length)
            {
                throw new InvalidDataException($"Saved block id {savedId} exceeds the supported material table.");
            }
            remap[savedId] = _blocks.TryGet(key, out BlockDefinition? definition)
                ? definition!.Id
                : (ushort)0;
        }

        int voxelCount = reader.ReadInt32();
        int sourceVoxelCount = checked(width * height * depth);
        if (voxelCount != sourceVoxelCount)
        {
            throw new InvalidDataException($"Invalid voxel count {voxelCount}.");
        }
        var sourceVoxels = new ushort[voxelCount];
        if (version >= 5)
        {
            for (int i = 0; i < voxelCount; i++) sourceVoxels[i] = reader.ReadUInt16();
        }
        else
        {
            byte[] legacyVoxels = reader.ReadBytes(voxelCount);
            if (legacyVoxels.Length != voxelCount)
            {
                throw new EndOfStreamException("World voxel data is truncated.");
            }
            for (int i = 0; i < voxelCount; i++) sourceVoxels[i] = legacyVoxels[i];
        }
        var microCells = new List<(int X, int Y, int Z, MicroVoxelSnapshot Snapshot)>();
        var physicsGrids = new List<SavedPhysicsGrid>();
        if (version >= 6)
        {
            int microCellCount = reader.ReadInt32();
            int worldCellCount = VoxelWorld.Width * VoxelWorld.Height * VoxelWorld.Depth;
            if (microCellCount < 0 || microCellCount > worldCellCount)
                throw new InvalidDataException("Invalid micro voxel cell count.");
            for (int cellIndex = 0; cellIndex < microCellCount; cellIndex++)
            {
                int x = reader.ReadInt32();
                int y = reader.ReadInt32();
                int z = reader.ReadInt32();
                int resolution = reader.ReadInt32();
                int boxCount = reader.ReadInt32();
                if (!world.IsInside(x, y, z)) throw new InvalidDataException("Micro voxel cell is outside the world.");
                if (resolution < 1 || resolution > MicroVoxelContainer.MaximumResolution)
                    throw new InvalidDataException("Invalid micro voxel resolution.");
                if (boxCount < 0 || boxCount > MicroVoxelContainer.MaximumBoxes)
                    throw new InvalidDataException("Invalid micro voxel box count.");

                var boxes = new List<MicroVoxelBox>(boxCount);
                for (int boxIndex = 0; boxIndex < boxCount; boxIndex++)
                {
                    int minX = reader.ReadInt32();
                    int minY = reader.ReadInt32();
                    int minZ = reader.ReadInt32();
                    int maxX = reader.ReadInt32();
                    int maxY = reader.ReadInt32();
                    int maxZ = reader.ReadInt32();
                    ushort savedMaterial = reader.ReadUInt16();
                    if (savedMaterial >= remap.Length)
                        throw new InvalidDataException($"Saved micro material id {savedMaterial} exceeds the supported material table.");
                    ushort material = remap[savedMaterial];
                    if (material != 0)
                        boxes.Add(new MicroVoxelBox(minX, minY, minZ, maxX, maxY, maxZ, material));
                }
                if (boxes.Count > 0)
                {
                    var snapshot = new MicroVoxelSnapshot(resolution, boxes);
                    _ = new MicroVoxelContainer(snapshot);
                    microCells.Add((x, y, z, snapshot));
                }
            }
        }
        if (version >= 7)
        {
            int gridCount = reader.ReadInt32();
            if (gridCount < 0 || gridCount > 4096) throw new InvalidDataException("Invalid physics grid count.");
            for (int gridIndex = 0; gridIndex < gridCount; gridIndex++)
            {
                Vector3 position = ReadVector3(reader);
                var orientation = new Quaternion(
                    reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                if (orientation.LengthSquared() < 0.5f) throw new InvalidDataException("Invalid physics grid orientation.");
                orientation = Quaternion.Normalize(orientation);
                Vector3 linearVelocity = ReadVector3(reader);
                Vector3 angularVelocity = ReadVector3(reader);
                bool sleeping = reader.ReadBoolean();
                bool fractureOnImpact = reader.ReadBoolean();
                int elementCount = reader.ReadInt32();
                if (elementCount <= 0 || elementCount > 1_000_000)
                    throw new InvalidDataException("Invalid physics grid element count.");
                var elements = new PhysicsGridElement[elementCount];
                for (int elementIndex = 0; elementIndex < elementCount; elementIndex++)
                {
                    int cellX = reader.ReadInt32();
                    int cellY = reader.ReadInt32();
                    int cellZ = reader.ReadInt32();
                    Vector3 minimum = ReadVector3(reader);
                    Vector3 maximum = ReadVector3(reader);
                    ushort savedMaterial = reader.ReadUInt16();
                    if (savedMaterial >= remap.Length)
                        throw new InvalidDataException("Physics grid material exceeds the supported table.");
                    ushort material = remap[savedMaterial];
                    if (material == 0) material = BlockRegistry.UnknownBlockId;
                    elements[elementIndex] = new PhysicsGridElement(
                        cellX, cellY, cellZ, minimum, maximum, material);
                }
                physicsGrids.Add(new SavedPhysicsGrid(
                    position, orientation, linearVelocity, angularVelocity, sleeping, fractureOnImpact, elements));
            }
        }
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("World chunk contains unexpected trailing data.");
        }

        for (int i = 0; i < sourceVoxels.Length; i++)
        {
            if (sourceVoxels[i] >= remap.Length)
                throw new InvalidDataException($"Saved voxel material id {sourceVoxels[i]} exceeds the supported material table.");
            sourceVoxels[i] = remap[sourceVoxels[i]];
        }
        var voxels = new ushort[VoxelWorld.Width * VoxelWorld.Height * VoxelWorld.Depth];
        for (int z = 0; z < depth; z++)
        for (int localY = 0; localY < height; localY++)
        for (int x = 0; x < width; x++)
        {
            int worldY = sourceMinimumY + localY;
            int sourceIndex = x + width * (localY + height * z);
            int destinationY = worldY - VoxelWorld.MinimumY;
            int destinationIndex = x + VoxelWorld.Width * (destinationY + VoxelWorld.Height * z);
            voxels[destinationIndex] = sourceVoxels[sourceIndex];
        }
        world.LoadRawData(voxels);
        world.LoadMicroVoxels(microCells);
        foreach (SavedPhysicsGrid savedGrid in physicsGrids)
        {
            PhysicsGrid grid = world.PhysicsGrids.Create(savedGrid.Elements, Vector3.Zero);
            grid.SetTransformAndVelocity(
                savedGrid.Position,
                savedGrid.Orientation,
                savedGrid.LinearVelocity,
                savedGrid.AngularVelocity);
            grid.IsSleeping = savedGrid.Sleeping;
            grid.FractureOnImpact = savedGrid.FractureOnImpact;
        }
        world.SetGenerationMetadata(generation);
        return new SavedWorldState(
            playerPosition,
            time,
            generation.Seed,
            health,
            oxygen,
            hunger,
            selectedHotbarSlot,
            inventory,
            completedObjectives,
            sawNight,
            generation);
    }

    private static PlayerMetadata CreatePlayerMetadata(
        Vector3 playerPosition,
        IInventory? inventory,
        float health,
        float oxygen,
        float hunger,
        IReadOnlyList<string>? completedObjectives,
        bool sawNight)
    {
        return new PlayerMetadata
        {
            FormatVersion = 1,
            Position = new[] { playerPosition.X, playerPosition.Y, playerPosition.Z },
            Health = health,
            Oxygen = oxygen,
            Hunger = hunger,
            SelectedHotbarSlot = inventory?.SelectedHotbarSlot ?? 0,
            InventorySlotCount = inventory?.SlotCount ?? 0,
            CompletedObjectives = completedObjectives?.ToArray() ?? Array.Empty<string>(),
            SawNight = sawNight,
            Inventory = inventory is null
                ? Array.Empty<PlayerItemMetadata>()
                : Enumerable.Range(0, inventory.SlotCount)
                    .Select(slot =>
                    {
                        ItemStack stack = inventory.GetSlot(slot);
                        return new PlayerItemMetadata
                        {
                            Slot = slot,
                            ItemKey = stack.ItemKey,
                            Count = stack.Count,
                            Damage = stack.Damage,
                        };
                    })
                    .Where(item => !string.IsNullOrWhiteSpace(item.ItemKey) && item.Count > 0)
                    .ToArray(),
        };
    }

    private void WritePlayerMetadata(string path, PlayerMetadata metadata) =>
        WriteJsonAtomically(path, metadata, keepBackup: false);

    private static int ReadSeed(string levelPath, int fallback)
    {
        if (!File.Exists(levelPath))
        {
            return fallback;
        }

        try
        {
            WorldLevelMetadata? metadata = JsonSerializer.Deserialize<WorldLevelMetadata>(
                File.ReadAllText(levelPath),
                JsonOptions());
            return metadata?.Seed ?? fallback;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not read world seed from '{levelPath}': {exception.Message}");
            return fallback;
        }
    }

    private static void WriteVector3(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static Vector3 ReadVector3(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static JsonSerializerOptions JsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public sealed class IncrementalSaveOperation
    {
        private readonly WorldSaveService _owner;
        private readonly VoxelWorld _world;
        private readonly ChunkCoordinate[] _coordinates;
        private readonly List<VoxelChunkSnapshot> _chunks;
        private readonly Vector3 _playerPosition;
        private readonly float _timeOfDay;
        private readonly IInventory? _inventory;
        private readonly float _health;
        private readonly float _oxygen;
        private readonly float _hunger;
        private readonly IReadOnlyList<string>? _completedObjectives;
        private readonly bool _sawNight;
        private int _nextChunk;

        public Task? WriteTask { get; private set; }
        public bool SnapshotComplete => _nextChunk >= _coordinates.Length;
        public bool IsCompleted => WriteTask?.IsCompleted == true;
        public int TotalChunkCount => _coordinates.Length;
        public int CapturedChunkCount => _nextChunk;
        public double SnapshotTotalMilliseconds { get; private set; }
        public double MaximumSnapshotStepMilliseconds { get; private set; }
        public double WriteMilliseconds { get; private set; }

        internal IncrementalSaveOperation(
            WorldSaveService owner,
            VoxelWorld world,
            Vector3 playerPosition,
            float timeOfDay,
            IInventory? inventory,
            float health,
            float oxygen,
            float hunger,
            IReadOnlyList<string>? completedObjectives,
            bool sawNight)
        {
            _owner = owner;
            _world = world;
            _coordinates = world.SnapshotGeneratedChunks()
                .OrderBy(chunk => chunk.Z)
                .ThenBy(chunk => chunk.X)
                .ToArray();
            _chunks = new List<VoxelChunkSnapshot>(_coordinates.Length);
            _playerPosition = playerPosition;
            _timeOfDay = timeOfDay;
            _inventory = inventory;
            _health = health;
            _oxygen = oxygen;
            _hunger = hunger;
            _completedObjectives = completedObjectives?.ToArray();
            _sawNight = sawNight;
        }

        public int CaptureNextChunks(int maximumChunks)
        {
            if (WriteTask is not null)
                throw new InvalidOperationException("The save snapshot is already being written.");
            int captured = 0;
            long started = Stopwatch.GetTimestamp();
            while (captured < Math.Max(0, maximumChunks) && _nextChunk < _coordinates.Length)
            {
                _chunks.Add(_world.SnapshotChunk(_coordinates[_nextChunk++]));
                captured++;
            }
            double elapsed = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            SnapshotTotalMilliseconds += elapsed;
            MaximumSnapshotStepMilliseconds = Math.Max(MaximumSnapshotStepMilliseconds, elapsed);
            return captured;
        }

        public void StartBackgroundWrite()
        {
            if (!SnapshotComplete)
                throw new InvalidOperationException("All Chunk snapshots must be captured before writing.");
            if (WriteTask is not null) return;
            CapturedWorldSave capture = _owner.CreateCapturedWorldSave(
                _world,
                _chunks,
                _playerPosition,
                _timeOfDay,
                _inventory,
                _health,
                _oxygen,
                _hunger,
                _completedObjectives,
                _sawNight);
            WriteTask = Task.Run(() =>
            {
                long started = Stopwatch.GetTimestamp();
                try
                {
                    _owner.WriteCapturedChunkDirectory(capture);
                }
                finally
                {
                    WriteMilliseconds = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                    _owner.LastSaveMilliseconds = WriteMilliseconds;
                }
            });
        }

        public void ThrowIfFailed() => WriteTask?.GetAwaiter().GetResult();
    }

    private sealed record CapturedWorldSave(
        int Seed,
        WorldGenerationSettings Generation,
        IReadOnlyList<VoxelChunkSnapshot> Chunks,
        IReadOnlyList<SavedPhysicsGrid> PhysicsGrids,
        PlayerMetadata Player,
        Vector3 PlayerPosition,
        float TimeOfDay,
        string[] RequiredBlockKeys,
        string[] BlockKeysById);

    private sealed class WorldLevelMetadata
    {
        public int FormatVersion { get; set; }
        public string Name { get; set; } = "world1";
        public string SaveGeneration { get; set; } = string.Empty;
        public int Seed { get; set; }
        public DateTimeOffset LastSavedUtc { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Depth { get; set; }
        public int ChunkSize { get; set; }
        public int RegionSizeChunks { get; set; }
        public int MinimumY { get; set; }
        public int MaximumY { get; set; }
        public float TimeOfDay { get; set; }
        public float[] PlayerPosition { get; set; } = new float[3];
        public string[] RequiredBlockKeys { get; set; } = Array.Empty<string>();
        public string[] BlockKeysById { get; set; } = Array.Empty<string>();
        public string[] Chunks { get; set; } = Array.Empty<string>();
        public string[] Regions { get; set; } = Array.Empty<string>();
        public string PhysicsFile { get; set; } = string.Empty;
        public string PlayerFile { get; set; } = string.Empty;
        public WorldGenerationSettings Generation { get; set; } = WorldGenerationSettings.Natural();
    }

    private sealed class PlayerMetadata
    {
        public int FormatVersion { get; set; }
        public float[] Position { get; set; } = new float[3];
        public float Health { get; set; } = 20f;
        public float Oxygen { get; set; } = 10f;
        public float Hunger { get; set; } = 20f;
        public int SelectedHotbarSlot { get; set; }
        public int InventorySlotCount { get; set; }
        public string[] CompletedObjectives { get; set; } = Array.Empty<string>();
        public bool SawNight { get; set; }
        public PlayerItemMetadata[] Inventory { get; set; } = Array.Empty<PlayerItemMetadata>();
    }

    private sealed class PlayerItemMetadata
    {
        public int Slot { get; set; }
        public string? ItemKey { get; set; }
        public int Count { get; set; }
        public int Damage { get; set; }
    }

    private sealed record SavedPhysicsGrid(
        Vector3 Position,
        Quaternion Orientation,
        Vector3 LinearVelocity,
        Vector3 AngularVelocity,
        bool Sleeping,
        bool FractureOnImpact,
        IReadOnlyList<PhysicsGridElement> Elements);
}
