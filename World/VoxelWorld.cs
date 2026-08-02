using System.Numerics;
using RTXSandbox.ModAPI;

namespace RTXSandbox.World;

public sealed class VoxelWorld : IVoxelWorld
{
    public const int Width = 64;
    public const int Height = 32;
    public const int Depth = 64;

    private readonly ushort[] _voxels = new ushort[Width * Height * Depth];
    private readonly BlockRegistry _blocks;
    private readonly Dictionary<int, MicroVoxelContainer> _microVoxels = new();
    public PhysicsGridWorld PhysicsGrids { get; } = new();
    public int Seed { get; private set; }
    public WorldGenerationSettings GenerationSettings { get; private set; }

    public event Action<int, int, int, VoxelMaterial>? VoxelChanged;
    public event Action<VoxelChangedEvent>? DetailedVoxelChanged;
    public event Action? WorldReloaded;
    public event Action? MicroVoxelsChanged;

    public ushort[] RawData => _voxels;
    public int MicroVoxelContainerCount => _microVoxels.Count;

    int IVoxelWorld.Width => Width;
    int IVoxelWorld.Height => Height;
    int IVoxelWorld.Depth => Depth;
    string IVoxelWorld.GenerationPreset => GenerationSettings.Preset.ToString();
    string IVoxelWorld.GenerationBiome => GenerationSettings.Biome.ToString();

    public VoxelWorld(
        bool generate = true,
        BlockRegistry? registry = null,
        int seed = 1337,
        WorldGenerationSettings? generationSettings = null)
    {
        _blocks = registry ?? new BlockRegistry();
        GenerationSettings = generationSettings?.Clone() ?? WorldGenerationSettings.Natural(seed);
        GenerationSettings.Seed = generationSettings?.Seed ?? seed;
        GenerationSettings.Validate();
        Seed = GenerationSettings.Seed;
        if (generate)
        {
            GenerateWorld();
        }
    }

    public bool IsInside(int x, int y, int z) =>
        x >= 0 && x < Width && y >= 0 && y < Height && z >= 0 && z < Depth;

    public VoxelMaterial Get(int x, int y, int z)
    {
        return IsInside(x, y, z) ? (VoxelMaterial)_voxels[Index(x, y, z)] : VoxelMaterial.Air;
    }

    public ushort GetBlockId(int x, int y, int z) => (ushort)Get(x, y, z);

    ushort IVoxelWorld.GetBlock(int x, int y, int z) => GetBlockId(x, y, z);

    public bool IsSolid(int x, int y, int z) =>
        Get(x, y, z) != VoxelMaterial.Air ||
        (TryGetMicroVoxels(x, y, z, out MicroVoxelContainer? micro) && !micro!.IsEmpty);

    public bool IsFluid(int x, int y, int z)
    {
        ushort blockId = GetBlockId(x, y, z);
        return blockId != 0 && _blocks.Get(blockId).IsFluid;
    }

    public bool IsSolidForCollision(int x, int y, int z)
    {
        if (y < 0 || x < 0 || x >= Width || z < 0 || z >= Depth)
        {
            return true;
        }

        if (y >= Height)
        {
            return false;
        }

        ushort materialId = GetBlockId(x, y, z);
        if (materialId != 0 && _blocks.Get(materialId).IsCollidable) return true;
        return TryGetMicroVoxels(x, y, z, out MicroVoxelContainer? micro) &&
               micro!.Boxes.Any(box => _blocks.Get(box.MaterialId).IsCollidable);
    }

    public bool Set(int x, int y, int z, VoxelMaterial material)
        => SetBlockId(x, y, z, (ushort)material);

    public bool SetBlockId(int x, int y, int z, ushort blockId)
    {
        if (!IsInside(x, y, z))
        {
            return false;
        }

        int index = Index(x, y, z);
        ushort oldBlockId = _voxels[index];
        bool removedMicro = _microVoxels.Remove(index);
        if (oldBlockId == blockId && !removedMicro)
        {
            return false;
        }

        if (oldBlockId != blockId)
        {
            _voxels[index] = blockId;
            VoxelChanged?.Invoke(x, y, z, (VoxelMaterial)blockId);
            DetailedVoxelChanged?.Invoke(new VoxelChangedEvent(x, y, z, oldBlockId, blockId));
        }
        if (removedMicro) MicroVoxelsChanged?.Invoke();
        return true;
    }

    bool IVoxelWorld.SetBlock(int x, int y, int z, ushort blockId) => SetBlockId(x, y, z, blockId);

    public void LoadRawData(ReadOnlySpan<ushort> data)
    {
        if (data.Length != _voxels.Length)
        {
            throw new ArgumentException($"Expected {_voxels.Length} voxel bytes, received {data.Length}.", nameof(data));
        }
        data.CopyTo(_voxels);
        _microVoxels.Clear();
        PhysicsGrids.Clear();
        WorldReloaded?.Invoke();
        MicroVoxelsChanged?.Invoke();
    }

    public void Regenerate(WorldGenerationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        GenerationSettings = settings.Clone();
        GenerationSettings.Validate();
        Seed = GenerationSettings.Seed;
        Array.Clear(_voxels, 0, _voxels.Length);
        _microVoxels.Clear();
        PhysicsGrids.Clear();
        GenerateWorld();
        WorldReloaded?.Invoke();
        MicroVoxelsChanged?.Invoke();
    }

    public void SetGenerationMetadata(WorldGenerationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        GenerationSettings = settings.Clone();
        GenerationSettings.Validate();
        Seed = GenerationSettings.Seed;
    }

    public int FindSurfaceY(int x, int z)
    {
        for (int y = Height - 1; y >= 0; y--)
        {
            ushort materialId = GetBlockId(x, y, z);
            if (materialId != 0 && _blocks.Get(materialId).IsCollidable)
            {
                return y;
            }
        }

        return 0;
    }

    public int FindTopVoxelY(int x, int z)
    {
        for (int y = Height - 1; y >= 0; y--)
        {
            if (IsSolid(x, y, z))
            {
                return y;
            }
        }

        return 0;
    }

    public bool TryGetMicroVoxels(int x, int y, int z, out MicroVoxelContainer? container)
    {
        if (!IsInside(x, y, z))
        {
            container = null;
            return false;
        }
        return _microVoxels.TryGetValue(Index(x, y, z), out container);
    }

    public MicroVoxelCellSnapshot CaptureCell(int x, int y, int z)
    {
        if (!IsInside(x, y, z)) throw new ArgumentOutOfRangeException(nameof(x));
        MicroVoxelSnapshot? micro = _microVoxels.TryGetValue(Index(x, y, z), out MicroVoxelContainer? container)
            ? container.Snapshot()
            : null;
        return new MicroVoxelCellSnapshot(x, y, z, GetBlockId(x, y, z), micro);
    }

    public bool RestoreCell(MicroVoxelCellSnapshot snapshot)
    {
        if (!IsInside(snapshot.X, snapshot.Y, snapshot.Z)) return false;
        int index = Index(snapshot.X, snapshot.Y, snapshot.Z);
        ushort oldBlock = _voxels[index];
        _microVoxels.Remove(index);
        _voxels[index] = snapshot.RegularBlockId;
        if (snapshot.MicroVoxels is not null && snapshot.MicroVoxels.Boxes.Count > 0)
        {
            _voxels[index] = 0;
            _microVoxels[index] = new MicroVoxelContainer(snapshot.MicroVoxels);
        }
        if (oldBlock != _voxels[index])
        {
            VoxelChanged?.Invoke(snapshot.X, snapshot.Y, snapshot.Z, (VoxelMaterial)_voxels[index]);
            DetailedVoxelChanged?.Invoke(new VoxelChangedEvent(snapshot.X, snapshot.Y, snapshot.Z, oldBlock, _voxels[index]));
        }
        MicroVoxelsChanged?.Invoke();
        return true;
    }

    public bool SetMicroVoxels(int x, int y, int z, MicroVoxelContainer? container)
    {
        if (!IsInside(x, y, z)) return false;
        int index = Index(x, y, z);
        ushort oldBlock = _voxels[index];
        if (container is null || container.IsEmpty)
        {
            bool removed = _microVoxels.Remove(index);
            if (removed) MicroVoxelsChanged?.Invoke();
            return removed;
        }

        if (container.TryGetFullBlock(out ushort fullMaterial))
        {
            return SetBlockId(x, y, z, fullMaterial);
        }

        _voxels[index] = 0;
        _microVoxels[index] = new MicroVoxelContainer(container.Snapshot());
        if (oldBlock != 0)
        {
            VoxelChanged?.Invoke(x, y, z, VoxelMaterial.Air);
            DetailedVoxelChanged?.Invoke(new VoxelChangedEvent(x, y, z, oldBlock, 0));
        }
        MicroVoxelsChanged?.Invoke();
        return true;
    }

    public IReadOnlyList<(int X, int Y, int Z, MicroVoxelSnapshot Snapshot)> SnapshotMicroVoxels()
    {
        var snapshots = new List<(int X, int Y, int Z, MicroVoxelSnapshot Snapshot)>(_microVoxels.Count);
        foreach ((int index, MicroVoxelContainer container) in _microVoxels.OrderBy(pair => pair.Key))
        {
            int x = index % Width;
            int yz = index / Width;
            int y = yz % Height;
            int z = yz / Height;
            snapshots.Add((x, y, z, container.Snapshot()));
        }
        return snapshots;
    }

    public void LoadMicroVoxels(IEnumerable<(int X, int Y, int Z, MicroVoxelSnapshot Snapshot)> snapshots)
    {
        _microVoxels.Clear();
        foreach ((int x, int y, int z, MicroVoxelSnapshot snapshot) in snapshots)
        {
            if (!IsInside(x, y, z)) throw new InvalidDataException("Micro voxel cell is outside the world.");
            var container = new MicroVoxelContainer(snapshot);
            if (container.IsEmpty) continue;
            int index = Index(x, y, z);
            _voxels[index] = 0;
            _microVoxels[index] = container;
        }
        MicroVoxelsChanged?.Invoke();
    }

    public void GetCollisionBoxes(int x, int y, int z, List<VoxelAabb> destination)
    {
        GetStaticCollisionBoxes(x, y, z, destination);
        if (y >= 0 && y < Height && x >= 0 && x < Width && z >= 0 && z < Depth)
            PhysicsGrids.AppendCollisionAabbs(x, y, z, destination);
    }

    internal void GetStaticCollisionBoxes(int x, int y, int z, List<VoxelAabb> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (y < 0 || x < 0 || x >= Width || z < 0 || z >= Depth)
        {
            destination.Add(new VoxelAabb(new Vector3(x, y, z), new Vector3(x + 1f, y + 1f, z + 1f), (ushort)VoxelMaterial.Bedrock));
            return;
        }
        if (y >= Height) return;

        ushort regular = GetBlockId(x, y, z);
        if (regular != 0 && _blocks.Get(regular).IsCollidable)
        {
            destination.Add(new VoxelAabb(new Vector3(x, y, z), new Vector3(x + 1f, y + 1f, z + 1f), regular));
        }
        if (TryGetMicroVoxels(x, y, z, out MicroVoxelContainer? micro))
        {
            foreach (MicroVoxelBox box in micro!.Boxes)
            {
                if (_blocks.Get(box.MaterialId).IsCollidable) destination.Add(micro.ToWorldAabb(x, y, z, box));
            }
        }
    }

    public bool IntersectsSolid(Vector3 minimum, Vector3 maximum)
    {
        const float epsilon = 0.0001f;
        int minX = (int)MathF.Floor(minimum.X);
        int minY = (int)MathF.Floor(minimum.Y);
        int minZ = (int)MathF.Floor(minimum.Z);
        int maxX = (int)MathF.Floor(maximum.X - epsilon);
        int maxY = (int)MathF.Floor(maximum.Y - epsilon);
        int maxZ = (int)MathF.Floor(maximum.Z - epsilon);

        var boxes = new List<VoxelAabb>(16);
        for (int z = minZ; z <= maxZ; z++)
        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            boxes.Clear();
            GetCollisionBoxes(x, y, z, boxes);
            if (boxes.Any(box => box.Intersects(minimum, maximum)))
            {
                return true;
            }
        }

        return false;
    }

    private void GenerateWorld()
    {
        switch (GenerationSettings.Preset)
        {
            case WorldPreset.Flat:
                GenerateFlat();
                break;
            case WorldPreset.Natural:
                GenerateNatural(pureNature: false);
                break;
            case WorldPreset.PureNature:
                GenerateNatural(pureNature: true);
                break;
            case WorldPreset.Void:
                GenerateVoid();
                break;
            case WorldPreset.Showcase:
                GenerateShowcase();
                break;
        }
    }

    private void GenerateFlat()
    {
        int surfaceY = GenerationSettings.FlatSurfaceHeight;
        ushort surface = ResolveBlock(GenerationSettings.SurfaceBlockKey, VoxelMaterial.Grass);
        ushort underground = ResolveBlock(GenerationSettings.UndergroundBlockKey, VoxelMaterial.Stone);
        for (int z = 0; z < Depth; z++)
        for (int x = 0; x < Width; x++)
        {
            SetSilently(x, 0, z, VoxelMaterial.Bedrock);
            for (int y = 1; y < surfaceY; y++) SetSilently(x, y, z, underground);
            SetSilently(x, surfaceY, z, surface);
        }

        if (GenerationSettings.GenerateLakes)
        {
            CarveRoundLake(14, 15, 5, surfaceY);
            CarveRoundLake(49, 45, 4, surfaceY);
        }
        if (GenerationSettings.GenerateVegetation)
        {
            BiomePreset[,] biomes = FilledBiomeMap(BiomePreset.Plains);
            GenerateVegetation(biomes);
        }
    }

    private void GenerateNatural(bool pureNature)
    {
        const int waterLevel = 7;
        var biomes = new BiomePreset[Width, Depth];
        var surfaces = new int[Width, Depth];
        var terrain = new TerrainSample[Width, Depth];

        for (int z = 0; z < Depth; z++)
        for (int x = 0; x < Width; x++)
        {
            TerrainSample sample = SampleTerrain(x, z, pureNature, waterLevel);
            if (GenerationSettings.Biome != BiomePreset.Auto)
            {
                sample = sample with { Biome = GenerationSettings.Biome };
                sample = sample with { Surface = SurfaceForBiome(sample, GenerationSettings.Biome, waterLevel) };
            }
            terrain[x, z] = sample;
            biomes[x, z] = sample.Biome;
            surfaces[x, z] = sample.Surface;
        }

        ushort grass = ResolveBlock("rtxsandbox:grass", VoxelMaterial.Grass);
        ushort dirt = ResolveBlock("rtxsandbox:dirt", VoxelMaterial.Dirt);
        ushort sandySoil = ResolveBlock("rtxsandbox:sandy_soil", VoxelMaterial.Sand);
        ushort blackSoil = ResolveBlock("rtxsandbox:black_soil", VoxelMaterial.Dirt);
        ushort swampMud = ResolveBlock("rtxsandbox:swamp_mud", VoxelMaterial.Mud);
        ushort clay = ResolveBlock("rtxsandbox:clay", VoxelMaterial.Dirt);
        ushort sand = ResolveBlock("rtxsandbox:sand", VoxelMaterial.Sand);
        ushort snow = ResolveBlock("rtxsandbox:snow", VoxelMaterial.Snow);
        ushort stone = ResolveBlock("rtxsandbox:stone", VoxelMaterial.Stone);
        ushort granite = ResolveBlock("rtxsandbox:granite", VoxelMaterial.Stone);
        ushort limestone = ResolveBlock("rtxsandbox:limestone", VoxelMaterial.Stone);
        ushort slate = ResolveBlock("rtxsandbox:slate", VoxelMaterial.Stone);
        ushort volcanic = ResolveBlock("rtxsandbox:volcanic_rock", VoxelMaterial.Stone);

        for (int z = 0; z < Depth; z++)
        for (int x = 0; x < Width; x++)
        {
            TerrainSample sample = terrain[x, z];
            BiomePreset biome = sample.Biome;
            int surface = surfaces[x, z];
            int slope = TerrainSlope(surfaces, x, z);
            bool riverBank = sample.River > 0.08f && surface <= waterLevel + 2;

            ushort exposedRock = sample.Geology < 0.28f ? granite : sample.Geology > 0.72f ? slate : limestone;
            ushort top = slope >= 4
                ? exposedRock
                : riverBank
                    ? (sample.Moisture > 0.62f ? clay : sandySoil)
                    : biome switch
                    {
                        BiomePreset.Desert => sand,
                        BiomePreset.Snow => snow,
                        BiomePreset.Mountains when surface >= 14 => snow,
                        BiomePreset.Mountains => exposedRock,
                        BiomePreset.Swamp => swampMud,
                        _ => grass,
                    };
            ushort subsoil = biome switch
            {
                BiomePreset.Desert => sandySoil,
                BiomePreset.Swamp => swampMud,
                BiomePreset.Forest or BiomePreset.BambooForest when sample.Moisture > 0.62f => blackSoil,
                _ => dirt,
            };
            if (riverBank) subsoil = sample.Moisture > 0.66f ? clay : sandySoil;

            for (int y = 0; y <= surface; y++)
            {
                int depth = surface - y;
                ushort material = y == 0
                    ? (ushort)VoxelMaterial.Bedrock
                    : y == surface ? top
                    : depth <= 2 + (int)MathF.Round(sample.Erosion * 2f) ? subsoil
                    : GeologicalLayer(x, y, z, surface, sample.Geology, stone, granite, limestone, slate, volcanic);
                SetSilently(x, y, z, material);
            }

            if (surface > 8 && (pureNature || biome is BiomePreset.Mountains or BiomePreset.Snow || sample.Continental > 0.58f))
            {
                for (int y = 3; y < surface - 2; y++)
                {
                    float cave = FractalNoise3D(x * 0.105f, y * 0.13f, z * 0.105f, 1223);
                    float tunnel = 1f - MathF.Abs(FractalNoiseSigned(x * 0.038f + y * 0.012f, z * 0.038f - y * 0.014f, 1297));
                    float threshold = 0.79f + Math.Clamp((waterLevel + 1 - y) * 0.018f, 0f, 0.08f);
                    if (cave * 0.76f + tunnel * 0.24f > threshold) SetSilently(x, y, z, VoxelMaterial.Air);
                }
            }

            if (GenerationSettings.GenerateLakes)
            {
                for (int y = surface + 1; y <= waterLevel; y++) SetSilently(x, y, z, VoxelMaterial.Water);
            }
        }

        if (GenerationSettings.GenerateVegetation) GenerateVegetation(biomes);
    }

    private TerrainSample SampleTerrain(int x, int z, bool pureNature, int waterLevel)
    {
        float warpX = FractalNoiseSigned(x * 0.021f + 17.3f, z * 0.021f - 8.1f, 43) * 9f;
        float warpZ = FractalNoiseSigned(x * 0.021f - 12.7f, z * 0.021f + 21.6f, 71) * 9f;
        float sampleX = x + warpX;
        float sampleZ = z + warpZ;
        float continental = FractalNoise(sampleX * 0.021f, sampleZ * 0.021f, 101);
        float erosion = FractalNoise(sampleX * 0.036f + 13f, sampleZ * 0.036f - 9f, 307);
        float ridge = 1f - MathF.Abs(FractalNoiseSigned(sampleX * 0.031f, sampleZ * 0.031f, 701));
        float detail = FractalNoise(sampleX * 0.115f, sampleZ * 0.115f, 811);
        float moisture = FractalNoise(sampleX * 0.025f, sampleZ * 0.025f, 1709);
        float temperature = FractalNoise(sampleX * 0.022f + 19f, sampleZ * 0.022f - 11f, 2017);
        float mountain = FractalNoise(sampleX * 0.029f - 7f, sampleZ * 0.029f + 23f, 2371);
        float geology = FractalNoise(sampleX * 0.047f + 4f, sampleZ * 0.047f - 16f, 2719);
        float riverField = MathF.Abs(FractalNoiseSigned(sampleX * 0.024f + 8.2f, sampleZ * 0.024f - 3.7f, 911));
        float river = GenerationSettings.GenerateLakes ? 1f - SmoothStep(0.035f, 0.13f, riverField) : 0f;

        BiomePreset biome = DetermineBiome(x, z, pureNature, moisture, temperature, mountain);
        float baseHeight = 7.2f + (continental - 0.5f) * 7f + (detail - 0.5f) * 1.8f;
        float mountainMask = SmoothStep(0.54f, 0.82f, mountain);
        float ridgeHeight = ridge * ridge * (7f + mountainMask * 9f) * (1.15f - erosion * 0.58f);
        float height = baseHeight + ridgeHeight * mountainMask;

        var preliminary = new TerrainSample(
            biome,
            0,
            continental,
            erosion,
            ridge,
            moisture,
            temperature,
            geology,
            river,
            detail);
        int surface = SurfaceForBiome(preliminary, biome, waterLevel, height);
        return preliminary with { Surface = Math.Clamp(surface, 3, Height - 7) };
    }

    private int SurfaceForBiome(TerrainSample sample, BiomePreset biome, int waterLevel, float? sourceHeight = null)
    {
        float height = sourceHeight ?? (7.2f + (sample.Continental - 0.5f) * 7f + (sample.Detail - 0.5f) * 1.8f);
        height = biome switch
        {
            BiomePreset.Mountains => height + sample.Ridge * sample.Ridge * 8f,
            BiomePreset.Desert => height * 0.76f + waterLevel * 0.25f + MathF.Sin((sample.Detail + sample.Geology) * MathF.PI * 5f) * 1.1f,
            BiomePreset.Swamp => Lerp(height, waterLevel - 1f + sample.Detail * 1.5f, 0.72f),
            BiomePreset.Snow => height + sample.Ridge * 2.8f,
            _ => height,
        };
        if (sample.River > 0f)
        {
            height = Lerp(height, waterLevel - 2f + sample.Detail * 0.7f, sample.River);
        }
        return Math.Clamp((int)MathF.Round(height), 3, Height - 7);
    }

    private BiomePreset DetermineBiome(
        int x,
        int z,
        bool pureNature,
        float moisture,
        float temperature,
        float mountain)
    {

        if (pureNature)
        {
            float position = x / (float)(Width - 1) + (moisture - 0.5f) * 0.16f;
            if (position < 0.16f) return BiomePreset.Desert;
            if (position < 0.34f) return BiomePreset.Plains;
            if (position < 0.56f) return BiomePreset.Forest;
            if (position < 0.72f) return z < Depth / 2 ? BiomePreset.BambooForest : BiomePreset.Swamp;
            if (position < 0.86f) return BiomePreset.Snow;
            return BiomePreset.Mountains;
        }

        if (mountain > 0.72f) return BiomePreset.Mountains;
        if (temperature < 0.27f) return BiomePreset.Snow;
        if (temperature > 0.68f && moisture < 0.42f) return BiomePreset.Desert;
        if (moisture > 0.76f) return BiomePreset.Swamp;
        if (moisture > 0.61f && temperature > 0.48f) return BiomePreset.BambooForest;
        if (moisture > 0.48f) return BiomePreset.Forest;
        return BiomePreset.Plains;
    }

    private int TerrainSlope(int[,] surfaces, int x, int z)
    {
        int center = surfaces[x, z];
        int maximum = 0;
        if (x > 0) maximum = Math.Max(maximum, Math.Abs(center - surfaces[x - 1, z]));
        if (x + 1 < Width) maximum = Math.Max(maximum, Math.Abs(center - surfaces[x + 1, z]));
        if (z > 0) maximum = Math.Max(maximum, Math.Abs(center - surfaces[x, z - 1]));
        if (z + 1 < Depth) maximum = Math.Max(maximum, Math.Abs(center - surfaces[x, z + 1]));
        return maximum;
    }

    private ushort GeologicalLayer(
        int x,
        int y,
        int z,
        int surface,
        float geology,
        ushort stone,
        ushort granite,
        ushort limestone,
        ushort slate,
        ushort volcanic)
    {
        int depth = surface - y;
        float pocket = FractalNoise3D(x * 0.075f, y * 0.085f, z * 0.075f, 2897);
        if (y <= 3 && pocket > 0.58f) return volcanic;
        if (depth > 8 && geology < 0.26f + (pocket - 0.5f) * 0.18f) return granite;
        if (geology > 0.74f - (pocket - 0.5f) * 0.16f) return slate;
        if (pocket > 0.64f && depth is > 3 and < 10) return limestone;
        return stone;
    }

    private void GenerateVegetation(BiomePreset[,] biomes)
    {
        ushort oak = ResolveBlock("rtxsandbox:oak_log", VoxelMaterial.Wood);
        ushort spruce = ResolveBlock("rtxsandbox:spruce_log", VoxelMaterial.Wood);
        ushort maple = ResolveBlock("rtxsandbox:maple_log", VoxelMaterial.Wood);
        ushort darkwood = ResolveBlock("rtxsandbox:darkwood_log", VoxelMaterial.Wood);
        for (int z = 3; z < Depth - 3; z++)
        for (int x = 3; x < Width - 3; x++)
        {
            int groundY = FindSurfaceY(x, z);
            if (groundY <= 0 || groundY >= Height - 7 || IsFluid(x, groundY, z) || IsFluid(x, groundY + 1, z)) continue;
            BiomePreset biome = biomes[x, z];
            uint hash = HashCoordinates(x, z);
            float grove = FractalNoise(x * 0.082f + 4f, z * 0.082f - 7f, 3413);
            uint baseFrequency = biome switch
            {
                BiomePreset.Forest => 15u,
                BiomePreset.BambooForest => 9u,
                BiomePreset.Swamp => 27u,
                BiomePreset.Snow => 61u,
                BiomePreset.Plains => 97u,
                _ => uint.MaxValue,
            };
            uint frequency = baseFrequency == uint.MaxValue
                ? uint.MaxValue
                : (uint)Math.Clamp((int)MathF.Round(baseFrequency * Lerp(1.5f, 0.62f, grove)), 7, 180);

            if (biome == BiomePreset.BambooForest && hash % frequency == 0)
            {
                int height = 4 + (int)((hash >> 8) % 5u);
                for (int y = groundY + 1; y <= Math.Min(groundY + height, Height - 1); y++)
                    SetSilently(x, y, z, VoxelMaterial.Bamboo);
            }
            else if (frequency != uint.MaxValue && hash % frequency == 0)
            {
                ushort trunk = biome switch
                {
                    BiomePreset.Snow => spruce,
                    BiomePreset.Swamp => darkwood,
                    BiomePreset.Forest when ((hash >> 10) & 3u) == 0u => maple,
                    BiomePreset.Forest when ((hash >> 10) & 3u) == 1u => spruce,
                    _ => oak,
                };
                bool conifer = biome == BiomePreset.Snow || trunk == spruce;
                BuildTree(x, groundY, z, 3 + (int)((hash >> 8) % (conifer ? 4u : 3u)), trunk, conifer);
            }
            else if (biome is BiomePreset.Plains or BiomePreset.Forest && hash % 43u == 0u)
            {
                SetSilently(x, groundY + 1, z, VoxelMaterial.Flower);
            }
        }
    }

    private void BuildTree(int x, int groundY, int z, int trunkHeight, ushort trunk, bool conifer)
    {
        for (int y = groundY + 1; y <= groundY + trunkHeight; y++) SetSilently(x, y, z, trunk);
        int crownY = groundY + trunkHeight;
        if (conifer)
        {
            for (int oy = -2; oy <= 2; oy++)
            {
                int radius = oy >= 2 ? 0 : oy == 1 ? 1 : 2;
                if (oy == -1) radius = 1;
                for (int oz = -radius; oz <= radius; oz++)
                for (int ox = -radius; ox <= radius; ox++)
                {
                    if (Math.Abs(ox) + Math.Abs(oz) > radius + 1) continue;
                    int leafX = x + ox;
                    int leafY = crownY + oy;
                    int leafZ = z + oz;
                    if (Get(leafX, leafY, leafZ) == VoxelMaterial.Air) SetSilently(leafX, leafY, leafZ, VoxelMaterial.Leaves);
                }
            }
            return;
        }

        for (int oz = -2; oz <= 2; oz++)
        for (int oy = -1; oy <= 2; oy++)
        for (int ox = -2; ox <= 2; ox++)
        {
            int distance = Math.Abs(ox) + Math.Abs(oz) + Math.Max(oy, 0);
            if (distance > 4 || (ox == 0 && oz == 0 && oy <= 0)) continue;
            if (distance >= 3 && ((HashCoordinates(x + ox, z + oz) ^ (uint)(oy * 265443576)) & 7u) == 0u) continue;
            int leafX = x + ox;
            int leafY = crownY + oy;
            int leafZ = z + oz;
            if (Get(leafX, leafY, leafZ) == VoxelMaterial.Air) SetSilently(leafX, leafY, leafZ, VoxelMaterial.Leaves);
        }
    }

    private void GenerateVoid()
    {
        int centerX = Width / 2;
        int centerZ = Depth / 2;
        for (int z = centerZ - 4; z <= centerZ + 4; z++)
        for (int x = centerX - 4; x <= centerX + 4; x++)
            SetSilently(x, 4, z, VoxelMaterial.WhiteConcrete);
        SetSilently(centerX, 3, centerZ, VoxelMaterial.Glow);
    }

    private void GenerateShowcase()
    {
        WorldGenerationSettings original = GenerationSettings;
        GenerationSettings = new WorldGenerationSettings
        {
            Preset = WorldPreset.Showcase,
            Seed = original.Seed,
            FlatSurfaceHeight = 4,
            SurfaceBlockKey = "rtxsandbox:white_concrete",
            UndergroundBlockKey = "rtxsandbox:stone",
            GenerateLakes = false,
            GenerateVegetation = false,
        };
        GenerateFlat();
        GenerationSettings = original;

        for (int z = 5; z <= 17; z++)
        for (int x = 5; x <= 17; x++)
        {
            SetSilently(x, 4, z, VoxelMaterial.Water);
            if (x is 5 or 17 || z is 5 or 17) SetSilently(x, 5, z, VoxelMaterial.Marble);
        }
        for (int z = 7; z <= 15; z += 4)
        for (int x = 25; x <= 41; x += 4)
        for (int y = 5; y <= 12; y++)
            SetSilently(x, y, z, (x + z) % 8 == 0 ? VoxelMaterial.Crystal : VoxelMaterial.Glass);

        BuildMaterialGallery();
        BuildLightingLab();
    }

    private void BuildLightingLab()
    {
        for (int z = 43; z <= 58; z++)
        for (int x = 6; x <= 22; x++)
        {
            SetSilently(x, 4, z, VoxelMaterial.BlackConcrete);
            if (x is 6 or 22 || z is 43 or 58)
                for (int y = 5; y <= 11; y++) SetSilently(x, y, z, VoxelMaterial.BlackConcrete);
        }
        SetSilently(10, 7, 49, VoxelMaterial.RedLamp);
        SetSilently(18, 7, 49, VoxelMaterial.BlueLamp);
        SetSilently(14, 7, 55, VoxelMaterial.PurpleLamp);
    }

    private void CarveRoundLake(int centerX, int centerZ, int radius, int surfaceY)
    {
        for (int z = centerZ - radius; z <= centerZ + radius; z++)
        for (int x = centerX - radius; x <= centerX + radius; x++)
        {
            int dx = x - centerX;
            int dz = z - centerZ;
            if (dx * dx + dz * dz > radius * radius) continue;
            SetSilently(x, surfaceY, z, VoxelMaterial.Water);
            if (dx * dx + dz * dz < (radius - 2) * (radius - 2))
            {
                SetSilently(x, surfaceY - 1, z, VoxelMaterial.Water);
            }
        }
    }

    private BiomePreset[,] FilledBiomeMap(BiomePreset biome)
    {
        var result = new BiomePreset[Width, Depth];
        for (int z = 0; z < Depth; z++)
        for (int x = 0; x < Width; x++) result[x, z] = biome;
        return result;
    }

    private ushort ResolveBlock(string key, VoxelMaterial fallback) =>
        _blocks.TryGet(key, out BlockDefinition? definition) && definition!.Id != 0
            ? definition.Id
            : (ushort)fallback;

    private float FractalNoise(float x, float z, int salt)
    {
        float sum = 0f;
        float amplitude = 0.57f;
        float total = 0f;
        for (int octave = 0; octave < 4; octave++)
        {
            sum += ValueNoise(x, z, salt + octave * 977) * amplitude;
            total += amplitude;
            x *= 2.03f;
            z *= 2.03f;
            amplitude *= 0.5f;
        }
        return sum / total;
    }

    private float FractalNoiseSigned(float x, float z, int salt) => FractalNoise(x, z, salt) * 2f - 1f;

    private float FractalNoise3D(float x, float y, float z, int salt)
    {
        float sum = 0f;
        float amplitude = 0.58f;
        float total = 0f;
        for (int octave = 0; octave < 3; octave++)
        {
            sum += ValueNoise3D(x, y, z, salt + octave * 977) * amplitude;
            total += amplitude;
            x *= 2.01f;
            y *= 2.01f;
            z *= 2.01f;
            amplitude *= 0.5f;
        }
        return sum / total;
    }

    private float ValueNoise(float x, float z, int salt)
    {
        int x0 = (int)MathF.Floor(x);
        int z0 = (int)MathF.Floor(z);
        float tx = Smooth(x - x0);
        float tz = Smooth(z - z0);
        float a = Hash01(x0, z0, salt);
        float b = Hash01(x0 + 1, z0, salt);
        float c = Hash01(x0, z0 + 1, salt);
        float d = Hash01(x0 + 1, z0 + 1, salt);
        return Lerp(Lerp(a, b, tx), Lerp(c, d, tx), tz);
    }

    private float ValueNoise3D(float x, float y, float z, int salt)
    {
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        int z0 = (int)MathF.Floor(z);
        float tx = Smooth(x - x0);
        float ty = Smooth(y - y0);
        float tz = Smooth(z - z0);
        float c000 = Hash013(x0, y0, z0, salt);
        float c100 = Hash013(x0 + 1, y0, z0, salt);
        float c010 = Hash013(x0, y0 + 1, z0, salt);
        float c110 = Hash013(x0 + 1, y0 + 1, z0, salt);
        float c001 = Hash013(x0, y0, z0 + 1, salt);
        float c101 = Hash013(x0 + 1, y0, z0 + 1, salt);
        float c011 = Hash013(x0, y0 + 1, z0 + 1, salt);
        float c111 = Hash013(x0 + 1, y0 + 1, z0 + 1, salt);
        float lower = Lerp(Lerp(c000, c100, tx), Lerp(c010, c110, tx), ty);
        float upper = Lerp(Lerp(c001, c101, tx), Lerp(c011, c111, tx), ty);
        return Lerp(lower, upper, tz);
    }

    private float Hash01(int x, int z, int salt)
    {
        uint value = (uint)(x * 73856093) ^ (uint)(z * 19349663) ^ (uint)(Seed * 83492791) ^ (uint)salt;
        value ^= value >> 16;
        value *= 0x7feb352d;
        value ^= value >> 15;
        return (value & 0x00ffffffu) / 16777215f;
    }

    private float Hash013(int x, int y, int z, int salt)
    {
        uint value = unchecked((uint)(x * 73856093)) ^
                     unchecked((uint)(y * 83492791)) ^
                     unchecked((uint)(z * 19349663)) ^
                     unchecked((uint)(Seed * 265443576)) ^
                     unchecked((uint)salt);
        value ^= value >> 16;
        value *= 0x7feb352d;
        value ^= value >> 15;
        value *= 0x846ca68b;
        value ^= value >> 16;
        return (value & 0x00ffffffu) / 16777215f;
    }

    private static float Smooth(float value) => value * value * (3f - 2f * value);
    private static float SmoothStep(float edge0, float edge1, float value) =>
        Smooth(Math.Clamp((value - edge0) / Math.Max(edge1 - edge0, 0.00001f), 0f, 1f));
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private void BuildMaterialGallery()
    {
        IReadOnlyList<BlockDefinition> materials = _blocks.PlaceableDefinitions();
        const int columns = 16;
        for (int i = 0; i < materials.Count; i++)
        {
            int x = 25 + i % columns;
            int z = 20 + i / columns;
            if (z >= 42) break;
            BlockDefinition material = materials[i];
            int height = material.Transmission > 0f || material.Metallic > 0.8f || material.Emission.LengthSquared() > 0.1f
                ? 2
                : 1;
            for (int y = 5; y < 5 + height; y++) SetSilently(x, y, z, material.Id);
        }
    }

    private void BuildRuin()
    {
        int centerX = 38;
        int centerZ = 27;
        int groundY = FindSurfaceY(centerX, centerZ) + 1;

        for (int x = centerX - 4; x <= centerX + 4; x++)
        {
            SetSilently(x, groundY, centerZ, VoxelMaterial.Brick);
        }

        for (int y = groundY + 1; y <= groundY + 4; y++)
        {
            SetSilently(centerX - 4, y, centerZ, VoxelMaterial.Brick);
            SetSilently(centerX + 4, y, centerZ, VoxelMaterial.Brick);
        }

        for (int x = centerX - 4; x <= centerX + 4; x++)
        {
            SetSilently(x, groundY + 5, centerZ, VoxelMaterial.Marble);
        }

        SetSilently(centerX, groundY + 1, centerZ, VoxelMaterial.Glow);
    }

    private void SetSilently(int x, int y, int z, VoxelMaterial material)
        => SetSilently(x, y, z, (ushort)material);

    private void SetSilently(int x, int y, int z, ushort blockId)
    {
        if (IsInside(x, y, z))
        {
            _voxels[Index(x, y, z)] = blockId;
        }
    }

    private uint HashCoordinates(int x, int z)
    {
        uint value = (uint)(x * 73856093) ^ (uint)(z * 19349663) ^ (uint)Seed;
        value ^= value >> 16;
        value *= 0x7feb352d;
        value ^= value >> 15;
        return value;
    }

    private static int Index(int x, int y, int z) => x + Width * (y + Height * z);

    private readonly record struct TerrainSample(
        BiomePreset Biome,
        int Surface,
        float Continental,
        float Erosion,
        float Ridge,
        float Moisture,
        float Temperature,
        float Geology,
        float River,
        float Detail);
}
