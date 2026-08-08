using System.Numerics;
using RTXSandbox.ModAPI;
using RTXSandbox.World;

namespace RTXSandbox.Rendering;

public enum MaterialFace
{
    Top,
    Side,
    Bottom,
}

public readonly record struct MaterialTileUv(Vector2 Minimum, Vector2 Maximum);

/// <summary>
/// Builds a deterministic, data-driven 16 x 16 texture set for every registered material.
/// The full 4096-entry registry is packed into one atlas so runtime block registration does
/// not change shader layouts or invalidate material IDs.
/// </summary>
public sealed class ProceduralMaterialAtlas
{
    public const int TileSize = 16;
    public const int FacesPerMaterial = 3;
    public const int TilesPerRow = 128;
    public const int Width = TileSize * TilesPerRow;
    public const int TileRows = (BlockRegistry.MaterialCapacity * FacesPerMaterial + TilesPerRow - 1) / TilesPerRow;
    public const int Height = TileSize * TileRows;

    public byte[] ColorPixels { get; } = new byte[Width * Height * 4];
    public byte[] DetailPixels { get; } = new byte[Width * Height * 2];

    public ProceduralMaterialAtlas(BlockRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        FillFallback();
        foreach (BlockDefinition definition in registry.Definitions)
        {
            Bake(definition);
        }
    }

    public MaterialTileUv GetTileUv(ushort materialId, MaterialFace face)
    {
        int tile = TileIndex(materialId, face);
        int tileX = tile % TilesPerRow * TileSize;
        int tileY = tile / TilesPerRow * TileSize;
        // Half-texel inset keeps ImGui's bilinear interpolation inside the selected tile.
        return new MaterialTileUv(
            new Vector2((tileX + 0.5f) / Width, (tileY + 0.5f) / Height),
            new Vector2((tileX + TileSize - 0.5f) / Width, (tileY + TileSize - 0.5f) / Height));
    }

    public Vector3 ReadColor(ushort materialId, MaterialFace face, int x, int y)
    {
        int pixel = PixelOffset(materialId, face, x, y);
        return new Vector3(
            ColorPixels[pixel * 4] / 255f,
            ColorPixels[pixel * 4 + 1] / 255f,
            ColorPixels[pixel * 4 + 2] / 255f);
    }

    public float ReadRoughness(ushort materialId, MaterialFace face, int x, int y) =>
        DetailPixels[PixelOffset(materialId, face, x, y) * 2] / 255f;

    private void FillFallback()
    {
        for (int pixel = 0; pixel < Width * Height; pixel++)
        {
            ColorPixels[pixel * 4] = 255;
            ColorPixels[pixel * 4 + 1] = 0;
            ColorPixels[pixel * 4 + 2] = 255;
            ColorPixels[pixel * 4 + 3] = 255;
            DetailPixels[pixel * 2] = 180;
            DetailPixels[pixel * 2 + 1] = 128;
        }
    }

    private void Bake(BlockDefinition definition)
    {
        string generator = ResolveGenerator(definition);
        float variation = definition.TextureVariation >= 0f
            ? definition.TextureVariation
            : DefaultVariation(generator);
        uint seed = StableStringHash(definition.Key) ^ unchecked((uint)definition.TextureSeed * 0x9e3779b9u);

        foreach (MaterialFace face in Enum.GetValues<MaterialFace>())
        for (int y = 0; y < TileSize; y++)
        for (int x = 0; x < TileSize; x++)
        {
            Texel texel = GenerateTexel(definition, generator, variation, seed, face, x, y);
            int pixel = PixelOffset(definition.Id, face, x, y);
            Vector3 color = Vector3.Clamp(texel.Color, Vector3.Zero, Vector3.One);
            ColorPixels[pixel * 4] = ToByte(color.X);
            ColorPixels[pixel * 4 + 1] = ToByte(color.Y);
            ColorPixels[pixel * 4 + 2] = ToByte(color.Z);
            ColorPixels[pixel * 4 + 3] = 255;
            DetailPixels[pixel * 2] = ToByte(Math.Clamp(texel.Roughness, 0.001f, 1f));
            DetailPixels[pixel * 2 + 1] = ToByte(Math.Clamp(texel.Height, 0f, 1f));
        }
    }

    private static Texel GenerateTexel(
        BlockDefinition definition,
        string generator,
        float variation,
        uint seed,
        MaterialFace face,
        int x,
        int y)
    {
        float u = (x + 0.5f) / TileSize;
        float v = (y + 0.5f) / TileSize;
        float fine = Noise(seed, x, y, (int)face);
        float broad = OctaveNoise(seed ^ 0x68bc21ebu, x, y, (int)face);
        float height = 0.5f + (broad - 0.5f) * variation * 1.7f;
        float roughness = definition.Roughness + (fine - 0.5f) * variation * 0.32f;
        Vector3 color = Vary(definition.Albedo, broad, variation);

        switch (generator)
        {
            case "solid":
                color = definition.Albedo;
                height = 0.5f;
                roughness = definition.Roughness;
                break;

            case "grass":
                if (face != MaterialFace.Top)
                {
                    Vector3 earth = face == MaterialFace.Bottom
                        ? new Vector3(0.25f, 0.125f, 0.05f)
                        : new Vector3(0.32f, 0.18f, 0.075f);
                    // Atlas row zero maps to the bottom edge of side faces in every
                    // renderer. Keep the grass transition at increasing V so grass is
                    // only on the top of the block instead of growing upward from below.
                    float mossLine = face == MaterialFace.Side ? SmoothStep(0.62f, 0.94f, v + (broad - 0.5f) * 0.28f) : 0f;
                    color = Vector3.Lerp(Vary(earth, broad, variation), Vary(definition.Albedo, fine, variation), mossLine);
                }
                else
                {
                    color = Vary(definition.Albedo, broad * 0.7f + fine * 0.3f, variation);
                }
                if (fine > 0.91f) color *= 0.72f;
                break;

            case "soil":
                color = Vary(definition.Albedo, broad, variation);
                if (fine > 0.88f) color *= 0.65f + Noise(seed, x, y, 71) * 0.2f;
                if (fine < 0.08f) color *= 1.15f;
                height += (fine - 0.5f) * 0.12f;
                break;

            case "stone":
                color = Vary(definition.Albedo, broad * 0.76f + fine * 0.24f, variation);
                if (fine > 0.92f) color *= 0.58f;
                if (Noise(seed ^ 0x1f123bb5u, x / 2, y / 2, 9) > 0.84f) color *= 1.13f;
                height += (fine - 0.5f) * 0.08f;
                break;

            case "ore":
                float luminance = Vector3.Dot(definition.Albedo, new Vector3(0.2126f, 0.7152f, 0.0722f));
                Vector3 rock = Vector3.Lerp(new Vector3(0.28f + luminance * 0.12f), definition.Albedo * 0.55f, 0.34f);
                float deposit = MathF.Max(fine, OctaveNoise(seed ^ 0xa511e9b3u, x + 3, y - 2, 22));
                color = deposit > 0.76f
                    ? Vary(definition.Albedo * 1.24f, deposit, variation * 0.8f)
                    : Vary(rock, broad, 0.18f);
                height += deposit > 0.76f ? 0.12f : (fine - 0.5f) * 0.05f;
                roughness += deposit > 0.76f ? -0.12f : 0.05f;
                break;

            case "sand":
                float ripple = MathF.Sin((v + broad * 0.08f) * MathF.PI * 10f) * 0.5f + 0.5f;
                color = Vary(definition.Albedo, fine * 0.35f + ripple * 0.65f, variation);
                if (fine > 0.94f) color *= 0.7f;
                height += (ripple - 0.5f) * 0.08f;
                break;

            case "wood":
                if (face is MaterialFace.Top or MaterialFace.Bottom)
                {
                    float dx = u - 0.5f;
                    float dy = v - 0.5f;
                    float rings = MathF.Sin(MathF.Sqrt(dx * dx + dy * dy) * 48f + broad * 3f) * 0.5f + 0.5f;
                    color = Vary(definition.Albedo, rings, variation * 1.2f);
                    height += (rings - 0.5f) * 0.09f;
                }
                else
                {
                    float grain = MathF.Sin((u + broad * 0.11f) * MathF.PI * 14f) * 0.5f + 0.5f;
                    color = Vary(definition.Albedo, grain * 0.75f + fine * 0.25f, variation * 1.25f);
                    height += (grain - 0.5f) * 0.12f;
                }
                break;

            case "leaves":
                color = Vary(definition.Albedo, broad * 0.55f + fine * 0.45f, variation * 1.35f);
                if (fine > 0.84f) color *= 0.46f;
                if (fine < 0.1f) color *= 1.18f;
                height += (fine - 0.5f) * 0.22f;
                break;

            case "brick":
                int row = y / 4;
                int shiftedX = (x + (row & 1) * 2) % TileSize;
                bool mortar = y % 4 == 0 || shiftedX % 8 == 0;
                color = mortar
                    ? new Vector3(0.31f, 0.3f, 0.285f)
                    : Vary(definition.Albedo, broad, variation);
                height = mortar ? 0.26f : 0.58f + (fine - 0.5f) * 0.07f;
                roughness = mortar ? 0.96f : roughness;
                break;

            case "marble":
                float wave = MathF.Sin((u * 1.3f + v * 0.72f) * MathF.PI * 7f + broad * 6f) * 0.5f + 0.5f;
                float vein = SmoothStep(0.76f, 0.94f, wave);
                color = Vector3.Lerp(Vary(definition.Albedo, broad, variation * 0.5f), definition.Albedo * 0.38f, vein * 0.72f);
                height += vein * 0.05f;
                break;

            case "panels":
                bool seam = x == 0 || y == 0 || (y == 8 && (x < 2 || x > 13));
                float panelGrain = MathF.Sin((u + broad * 0.06f) * MathF.PI * 12f) * 0.5f + 0.5f;
                color = seam ? definition.Albedo * 0.34f : Vary(definition.Albedo, panelGrain, variation);
                height = seam ? 0.28f : 0.56f + (panelGrain - 0.5f) * 0.05f;
                break;

            case "tiles":
                bool grout = x % 8 == 0 || y % 8 == 0;
                color = grout ? definition.Albedo * 0.3f : Vary(definition.Albedo, broad, variation * 0.55f);
                height = grout ? 0.25f : 0.58f;
                roughness = grout ? MathF.Max(roughness, 0.9f) : roughness;
                break;

            case "patina":
                float patina = OctaveNoise(seed ^ 0xc2b2ae35u, x, y, 18);
                color = Vector3.Lerp(Vary(definition.Albedo, broad, variation), new Vector3(0.055f, 0.42f, 0.31f), SmoothStep(0.64f, 0.86f, patina) * 0.8f);
                height += (patina - 0.5f) * 0.11f;
                break;

            case "grid":
                bool line = x % 4 == 0 || y % 4 == 0;
                color = line ? definition.Albedo * 1.35f : definition.Albedo * 0.52f;
                height = line ? 0.63f : 0.35f;
                break;

            case "circuit":
                bool trace = ((x + (y / 4) * 3) % 7 == 0) || (y % 6 == 0 && x > 3 && x < 13);
                color = trace ? new Vector3(0.12f, 0.9f, 0.34f) : definition.Albedo * (0.48f + broad * 0.18f);
                height = trace ? 0.68f : 0.4f;
                roughness = trace ? 0.22f : roughness;
                break;

            case "fabric":
                float weave = ((x + y) & 1) == 0 ? 1.12f : 0.78f;
                color = definition.Albedo * weave;
                height = ((x ^ y) & 1) == 0 ? 0.62f : 0.42f;
                roughness = MathF.Max(roughness, 0.88f);
                break;

            case "glass":
                color = Vary(definition.Albedo, broad, variation * 0.28f);
                height = 0.5f;
                roughness = definition.Roughness;
                break;

            case "frosted_glass":
                // Frosted glass has visible medium-sized grains. Quantizing the
                // noise coordinates keeps it distinct from the much finer matte
                // finish while remaining finer than the 4x4 mosaic cells.
                int frostX = x / 2;
                int frostY = y / 2;
                float frostCloud = OctaveNoise(seed ^ 0xd1b54a35u, frostX, frostY, 83);
                float frostGrain = Noise(seed ^ 0x94d049bbu, frostX, frostY, 97);
                color = Vector3.Lerp(
                    Vary(definition.Albedo, frostCloud, variation * 0.22f),
                    Vector3.One,
                    0.04f + frostGrain * 0.1f);
                height = 0.3f + frostCloud * 0.42f + (frostGrain - 0.5f) * 0.2f;
                roughness = Math.Clamp(definition.Roughness + (frostGrain - 0.5f) * 0.2f, 0.45f, 1f);
                break;

            case "matte_glass":
                // Matte glass uses per-texel grain: its surface is cloudy, but the
                // individual structure is finer than frosted and mosaic glass.
                float matteGrain = Noise(seed ^ 0x369dea0fu, x, y, 109);
                float matteCloud = OctaveNoise(seed ^ 0x7f4a7c15u, x, y, 131);
                color = Vary(definition.Albedo, matteCloud * 0.35f + matteGrain * 0.65f, variation * 0.08f);
                height = 0.44f + (matteGrain - 0.5f) * 0.12f;
                roughness = Math.Clamp(definition.Roughness + (matteGrain - 0.5f) * 0.07f, 0.78f, 1f);
                break;

            case "mosaic_glass":
                int mosaicX = x / 4;
                int mosaicY = y / 4;
                bool mosaicJoint = x % 4 == 0 || y % 4 == 0;
                Vector3 mosaicTint = ((mosaicX + mosaicY * 3) & 3) switch
                {
                    0 => definition.Albedo * 0.78f,
                    1 => new Vector3(0.72f, 0.105f, 0.065f),
                    2 => new Vector3(0.07f, 0.54f, 0.19f),
                    _ => new Vector3(0.52f, 0.13f, 0.73f),
                };
                color = mosaicJoint
                    ? new Vector3(0.055f, 0.065f, 0.075f)
                    : Vary(mosaicTint, broad, variation * 0.3f);
                height = mosaicJoint ? 0.18f : 0.58f + (broad - 0.5f) * 0.12f;
                roughness = mosaicJoint ? 0.82f : definition.Roughness + (fine - 0.5f) * 0.1f;
                break;

            case "acrylic_glass":
                float acrylicBand = MathF.Sin((u * 0.74f + v * 0.21f + broad * 0.04f) * MathF.PI * 18f);
                bool acrylicScratch = Noise(seed ^ 0x7f4a7c15u, x / 3, y, 127) > 0.93f;
                color = Vary(definition.Albedo, broad, variation * 0.12f) * (acrylicScratch ? 0.88f : 1f);
                height = 0.5f + acrylicBand * 0.025f + (acrylicScratch ? 0.09f : 0f);
                roughness = Math.Clamp(definition.Roughness + (acrylicScratch ? 0.16f : 0f), 0.03f, 0.32f);
                break;

            case "ribbed_glass":
                float rib = MathF.Sin(u * MathF.PI * 8f) * 0.5f + 0.5f;
                color = Vary(definition.Albedo, rib * 0.7f + broad * 0.3f, variation * 0.25f);
                height = 0.22f + rib * 0.62f;
                roughness = Math.Clamp(definition.Roughness + (1f - rib) * 0.12f, 0.16f, 0.62f);
                break;

            case "etched_glass":
                float etchedWave = MathF.Sin((u + v) * MathF.PI * 8f) *
                                    MathF.Sin((u - v) * MathF.PI * 8f);
                float etchedMask = SmoothStep(0.28f, 0.72f, MathF.Abs(etchedWave));
                color = Vector3.Lerp(definition.Albedo * 0.82f, definition.Albedo * 1.08f, etchedMask);
                height = 0.35f + etchedMask * 0.3f + (fine - 0.5f) * 0.08f;
                roughness = Math.Clamp(definition.Roughness + (1f - etchedMask) * 0.22f, 0.36f, 0.92f);
                break;

            default:
                color = Vary(definition.Albedo, broad, variation);
                break;
        }

        return new Texel(color, roughness, height);
    }

    private static string ResolveGenerator(BlockDefinition definition)
    {
        if (!string.Equals(definition.TextureGenerator, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return definition.TextureGenerator.ToLowerInvariant();
        }

        if (definition.IsTransparent || definition.Transmission > 0f) return "glass";
        if (definition.Tags.Contains("ore", StringComparer.OrdinalIgnoreCase)) return "ore";
        if (definition.Tags.Contains("soil", StringComparer.OrdinalIgnoreCase) ||
            definition.Tags.Contains("earth", StringComparer.OrdinalIgnoreCase)) return "soil";
        if (definition.Category.Contains("Concrete", StringComparison.OrdinalIgnoreCase) ||
            definition.Category.Contains("Reference", StringComparison.OrdinalIgnoreCase)) return "solid";

        return definition.SurfacePattern switch
        {
            BlockSurfacePattern.Grass => "grass",
            BlockSurfacePattern.Stone => "stone",
            BlockSurfacePattern.Sand => "sand",
            BlockSurfacePattern.Wood => "wood",
            BlockSurfacePattern.Leaves => "leaves",
            BlockSurfacePattern.Brick => "brick",
            BlockSurfacePattern.CopperPatina => "patina",
            BlockSurfacePattern.Obsidian => "stone",
            BlockSurfacePattern.Marble => "marble",
            BlockSurfacePattern.Ice => "glass",
            BlockSurfacePattern.Tiles => "tiles",
            BlockSurfacePattern.Panels => "panels",
            BlockSurfacePattern.Circuit => "circuit",
            BlockSurfacePattern.Grid => "grid",
            BlockSurfacePattern.Checker => "tiles",
            BlockSurfacePattern.Fabric => "fabric",
            _ when definition.Category.StartsWith("Nature", StringComparison.OrdinalIgnoreCase) => "stone",
            _ when definition.Category.StartsWith("Wood", StringComparison.OrdinalIgnoreCase) => "wood",
            _ => "solid",
        };
    }

    private static float DefaultVariation(string generator) => generator switch
    {
        "solid" => 0f,
        "glass" => 0.025f,
        "frosted_glass" => 0.65f,
        "matte_glass" => 0.38f,
        "mosaic_glass" or "ribbed_glass" or "etched_glass" => 0.42f,
        "acrylic_glass" => 0.16f,
        "grid" or "circuit" or "tiles" or "panels" => 0.055f,
        "brick" => 0.09f,
        "sand" => 0.12f,
        "marble" or "patina" => 0.15f,
        "wood" => 0.17f,
        "grass" or "soil" => 0.2f,
        "leaves" => 0.25f,
        "ore" or "stone" => 0.18f,
        _ => 0.08f,
    };

    private static Vector3 Vary(Vector3 color, float noise, float amount)
    {
        float multiplier = 1f + (noise - 0.5f) * amount * 2f;
        return color * multiplier;
    }

    private static float OctaveNoise(uint seed, int x, int y, int channel)
    {
        float fine = Noise(seed, x, y, channel);
        float medium = Noise(seed ^ 0x85ebca6bu, FloorDiv(x, 2), FloorDiv(y, 2), channel + 17);
        float broad = Noise(seed ^ 0xc2b2ae35u, FloorDiv(x, 4), FloorDiv(y, 4), channel + 31);
        return fine * 0.24f + medium * 0.34f + broad * 0.42f;
    }

    private static float Noise(uint seed, int x, int y, int channel)
    {
        uint value = seed;
        value ^= unchecked((uint)x) * 0x9e3779b9u;
        value ^= unchecked((uint)y) * 0x85ebca6bu;
        value ^= unchecked((uint)channel) * 0xc2b2ae35u;
        value ^= value >> 16;
        value *= 0x7feb352du;
        value ^= value >> 15;
        value *= 0x846ca68bu;
        value ^= value >> 16;
        return (value & 0x00ffffffu) / 16777215f;
    }

    private static uint StableStringHash(string value)
    {
        uint hash = 2166136261u;
        foreach (char character in value)
        {
            hash ^= character;
            hash *= 16777619u;
        }
        return hash;
    }

    private static int FloorDiv(int value, int divisor) =>
        value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = Math.Clamp((value - edge0) / Math.Max(edge1 - edge0, 0.00001f), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

    private static int TileIndex(ushort materialId, MaterialFace face) =>
        Math.Min((int)materialId, BlockRegistry.MaterialCapacity - 1) * FacesPerMaterial + (int)face;

    private static int PixelOffset(ushort materialId, MaterialFace face, int x, int y)
    {
        x = Math.Clamp(x, 0, TileSize - 1);
        y = Math.Clamp(y, 0, TileSize - 1);
        int tile = TileIndex(materialId, face);
        int atlasX = tile % TilesPerRow * TileSize + x;
        int atlasY = tile / TilesPerRow * TileSize + y;
        return atlasY * Width + atlasX;
    }

    private readonly record struct Texel(Vector3 Color, float Roughness, float Height);
}
