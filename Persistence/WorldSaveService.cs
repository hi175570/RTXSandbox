using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RTXSandbox.ModAPI;
using RTXSandbox.World;

namespace RTXSandbox.Persistence;

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
    private const int Version = 7;

    private readonly BlockRegistry _blocks;
    private readonly bool _singleFileMode;
    private readonly string? _legacySavePath;

    public string SaveDirectory { get; }
    public string SavePath { get; }
    public string BackupPath => SavePath + ".bak";
    public string TemporaryPath => SavePath + ".tmp";
    public string? LevelPath { get; }
    public string? PlayerPath { get; }
    public int WorldSeed { get; }
    public string WorldName { get; }
    public string LastLoadSource { get; private set; } = "none";
    public bool RecoveredLastLoad { get; private set; }

    public WorldSaveService(
        BlockRegistry blocks,
        string? savePath = null,
        int preferredSeed = 1337,
        string worldName = "world1")
    {
        _blocks = blocks;
        _singleFileMode = !string.IsNullOrWhiteSpace(savePath);
        if (_singleFileMode)
        {
            SavePath = Path.GetFullPath(savePath!);
            SaveDirectory = Path.GetDirectoryName(SavePath) ?? AppContext.BaseDirectory;
            LevelPath = null;
            PlayerPath = null;
            WorldSeed = preferredSeed;
            WorldName = Path.GetFileNameWithoutExtension(SavePath);
            return;
        }

        WorldName = SanitizeWorldName(worldName);
        SaveDirectory = Path.Combine(AppContext.BaseDirectory, "Saves", WorldName);
        SavePath = Path.Combine(SaveDirectory, "chunks", "0_0.chunk");
        LevelPath = Path.Combine(SaveDirectory, "level.json");
        PlayerPath = Path.Combine(SaveDirectory, "player.json");
        _legacySavePath = string.Equals(WorldName, "world1", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(AppContext.BaseDirectory, "Saves", "world1.rtxw")
            : null;
        WorldSeed = ReadSeed(LevelPath, preferredSeed);
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

        if (!_singleFileMode && LevelPath is not null)
        {
            WriteLevelMetadata(LevelPath, world, playerPosition, timeOfDay);
            if (PlayerPath is not null)
            {
                WritePlayerMetadata(
                    PlayerPath,
                    playerPosition,
                    inventory,
                    health,
                    oxygen,
                    hunger,
                    completedObjectives,
                    sawNight);
            }
        }
    }

    public bool TryLoad(VoxelWorld world, out SavedWorldState state, out string? error)
    {
        state = default;
        error = null;
        LastLoadSource = "none";
        RecoveredLastLoad = false;

        var candidates = new List<(string Path, bool Recovery)>();
        if (File.Exists(TemporaryPath)) candidates.Add((TemporaryPath, true));
        if (File.Exists(SavePath)) candidates.Add((SavePath, false));
        if (File.Exists(BackupPath)) candidates.Add((BackupPath, true));
        if (!_singleFileMode && _legacySavePath is not null && File.Exists(_legacySavePath))
        {
            candidates.Add((_legacySavePath, true));
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        var failures = new List<string>();
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

        writer.Write(world.RawData.Length);
        foreach (ushort blockId in world.RawData)
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
        if (width != VoxelWorld.Width || height != VoxelWorld.Height || depth != VoxelWorld.Depth)
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
        if (voxelCount != VoxelWorld.Width * VoxelWorld.Height * VoxelWorld.Depth)
        {
            throw new InvalidDataException($"Invalid voxel count {voxelCount}.");
        }
        var voxels = new ushort[voxelCount];
        if (version >= 5)
        {
            for (int i = 0; i < voxelCount; i++) voxels[i] = reader.ReadUInt16();
        }
        else
        {
            byte[] legacyVoxels = reader.ReadBytes(voxelCount);
            if (legacyVoxels.Length != voxelCount)
            {
                throw new EndOfStreamException("World voxel data is truncated.");
            }
            for (int i = 0; i < voxelCount; i++) voxels[i] = legacyVoxels[i];
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

        for (int i = 0; i < voxels.Length; i++)
        {
            if (voxels[i] >= remap.Length)
                throw new InvalidDataException($"Saved voxel material id {voxels[i]} exceeds the supported material table.");
            voxels[i] = remap[voxels[i]];
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

    private void WriteLevelMetadata(string path, VoxelWorld world, Vector3 playerPosition, float timeOfDay)
    {
        var metadata = new WorldLevelMetadata
        {
            FormatVersion = 2,
            Name = "world1",
            Seed = world.Seed,
            LastSavedUtc = DateTimeOffset.UtcNow,
            Width = VoxelWorld.Width,
            Height = VoxelWorld.Height,
            Depth = VoxelWorld.Depth,
            TimeOfDay = timeOfDay,
            PlayerPosition = new[] { playerPosition.X, playerPosition.Y, playerPosition.Z },
            RequiredBlockKeys = _blocks.Definitions.Select(definition => definition.Key).ToArray(),
            Chunks = new[] { "chunks/0_0.chunk" },
            Generation = world.GenerationSettings.Clone(),
        };

        string temporaryLevelPath = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? SaveDirectory);
        File.WriteAllText(temporaryLevelPath, JsonSerializer.Serialize(metadata, JsonOptions()));
        File.Move(temporaryLevelPath, path, overwrite: true);
    }

    private void WritePlayerMetadata(
        string path,
        Vector3 playerPosition,
        IInventory? inventory,
        float health,
        float oxygen,
        float hunger,
        IReadOnlyList<string>? completedObjectives,
        bool sawNight)
    {
        var metadata = new PlayerMetadata
        {
            FormatVersion = 1,
            Position = new[] { playerPosition.X, playerPosition.Y, playerPosition.Z },
            Health = health,
            Oxygen = oxygen,
            Hunger = hunger,
            SelectedHotbarSlot = inventory?.SelectedHotbarSlot ?? 0,
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

        string temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(metadata, JsonOptions()));
        File.Move(temporaryPath, path, overwrite: true);
    }

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

    private sealed class WorldLevelMetadata
    {
        public int FormatVersion { get; set; }
        public string Name { get; set; } = "world1";
        public int Seed { get; set; }
        public DateTimeOffset LastSavedUtc { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Depth { get; set; }
        public float TimeOfDay { get; set; }
        public float[] PlayerPosition { get; set; } = new float[3];
        public string[] RequiredBlockKeys { get; set; } = Array.Empty<string>();
        public string[] Chunks { get; set; } = Array.Empty<string>();
        public WorldGenerationSettings Generation { get; set; } = WorldGenerationSettings.Natural();
    }

    private sealed class PlayerMetadata
    {
        public int FormatVersion { get; set; }
        public float[] Position { get; set; } = new float[3];
        public float Health { get; set; }
        public float Oxygen { get; set; }
        public float Hunger { get; set; }
        public int SelectedHotbarSlot { get; set; }
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
