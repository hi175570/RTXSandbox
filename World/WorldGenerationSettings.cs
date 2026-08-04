namespace RTXSandbox.World;

public enum WorldPreset
{
    Flat,
    Natural,
    PureNature,
    Void,
    Showcase,
}

public enum BiomePreset
{
    Auto,
    Plains,
    Forest,
    BambooForest,
    Desert,
    Snow,
    Mountains,
    Swamp,
}

public sealed class WorldGenerationSettings
{
    public WorldPreset Preset { get; set; } = WorldPreset.Natural;
    public BiomePreset Biome { get; set; } = BiomePreset.Auto;
    public int Seed { get; set; } = 1337;
    public int FlatSurfaceHeight { get; set; } = 6;
    public string SurfaceBlockKey { get; set; } = "rtxsandbox:grass";
    public string UndergroundBlockKey { get; set; } = "rtxsandbox:stone";
    public bool GenerateLakes { get; set; } = true;
    public bool GenerateVegetation { get; set; } = true;

    public WorldGenerationSettings Clone() => new()
    {
        Preset = Preset,
        Biome = Biome,
        Seed = Seed,
        FlatSurfaceHeight = FlatSurfaceHeight,
        SurfaceBlockKey = SurfaceBlockKey,
        UndergroundBlockKey = UndergroundBlockKey,
        GenerateLakes = GenerateLakes,
        GenerateVegetation = GenerateVegetation,
    };

    public void Validate()
    {
        FlatSurfaceHeight = Math.Clamp(FlatSurfaceHeight, VoxelWorld.MinimumY + 1, 256);
        if (string.IsNullOrWhiteSpace(SurfaceBlockKey)) SurfaceBlockKey = "rtxsandbox:grass";
        if (string.IsNullOrWhiteSpace(UndergroundBlockKey)) UndergroundBlockKey = "rtxsandbox:stone";
    }

    public static WorldGenerationSettings Natural(int seed = 1337) => new()
    {
        Preset = WorldPreset.Natural,
        Biome = BiomePreset.Auto,
        Seed = seed,
    };
}
