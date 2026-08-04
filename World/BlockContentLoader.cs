using System.Numerics;
using System.Text.Json;
using RTXSandbox.Infrastructure;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using RTXSandbox.ModAPI;

namespace RTXSandbox.World;

public sealed record LoadedContentPack(
    string Id,
    string Name,
    string Version,
    int Priority,
    int BlockCount,
    string Directory,
    bool Enabled,
    bool IsBase,
    string? Error = null);

public static class BlockContentLoader
{
    public static string DefaultBaseContentPath =>
        Path.Combine(RuntimePaths.OutputRoot, "Content", "Base", "base.blocks.json");

    public static string DefaultContentPacksPath =>
        Path.Combine(RuntimePaths.OutputRoot, "ContentPacks");

    public static IReadOnlyList<LoadedContentPack> LoadInto(
        BlockRegistry registry,
        string? baseContentPath = null,
        string? contentPacksPath = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        baseContentPath ??= DefaultBaseContentPath;
        contentPacksPath ??= DefaultContentPacksPath;

        var loaded = new List<LoadedContentPack>();
        BlockPackDocument basePack = ReadBlockDocument(baseContentPath);
        int baseCount = RegisterDocument(registry, basePack, forceAll: false);
        string baseDirectory = Path.GetDirectoryName(baseContentPath) ?? AppContext.BaseDirectory;
        string baseBlocksDirectory = Path.Combine(baseDirectory, "blocks");
        if (Directory.Exists(baseBlocksDirectory))
        {
            foreach (string path in Directory.GetFiles(baseBlocksDirectory, "*.json")
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                baseCount += RegisterDocument(registry, ReadBlockDocument(path), forceAll: false);
            }
        }
        loaded.Add(new LoadedContentPack(
            basePack.Id,
            basePack.Name,
            basePack.Version,
            int.MinValue,
            baseCount,
            baseDirectory,
            Enabled: true,
            IsBase: true));

        if (!Directory.Exists(contentPacksPath))
        {
            Directory.CreateDirectory(contentPacksPath);
            return loaded;
        }

        var candidates = new List<(PackManifest Manifest, string Directory)>();
        foreach (string directory in Directory.GetDirectories(contentPacksPath))
        {
            string manifestPath = Path.Combine(directory, "pack.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                PackManifest manifest = JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(manifestPath), JsonOptions())
                    ?? throw new InvalidDataException("The manifest is empty.");
                if (manifest.Enabled)
                {
                    candidates.Add((manifest, directory));
                }
                else
                {
                    loaded.Add(new LoadedContentPack(
                        manifest.Id,
                        manifest.Name,
                        manifest.Version,
                        manifest.Priority,
                        0,
                        directory,
                        Enabled: false,
                        IsBase: false));
                }
            }
            catch (Exception exception)
            {
                loaded.Add(new LoadedContentPack(
                    Path.GetFileName(directory),
                    Path.GetFileName(directory),
                    "?",
                    0,
                    0,
                    directory,
                    Enabled: false,
                    IsBase: false,
                    Error: $"Invalid pack.json: {exception.Message}"));
            }
        }

        foreach ((PackManifest manifest, string directory) in candidates
                     .OrderBy(candidate => candidate.Manifest.Priority)
                     .ThenBy(candidate => candidate.Manifest.Id, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                int count = 0;
                string blocksDirectory = Path.Combine(directory, "blocks");
                IEnumerable<string> blockFiles = Directory.Exists(blocksDirectory)
                    ? Directory.GetFiles(blocksDirectory, "*.json").OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    : Enumerable.Empty<string>();
                string rootBlocksFile = Path.Combine(directory, "blocks.json");
                if (File.Exists(rootBlocksFile))
                {
                    blockFiles = new[] { rootBlocksFile }.Concat(blockFiles);
                }

                foreach (string path in blockFiles)
                {
                    count += RegisterDocument(registry, ReadBlockDocument(path), manifest.ForceOverrides);
                }
                loaded.Add(new LoadedContentPack(
                    manifest.Id,
                    manifest.Name,
                    manifest.Version,
                    manifest.Priority,
                    count,
                    directory,
                    Enabled: true,
                    IsBase: false));
            }
            catch (Exception exception)
            {
                loaded.Add(new LoadedContentPack(
                    manifest.Id,
                    manifest.Name,
                    manifest.Version,
                    manifest.Priority,
                    0,
                    directory,
                    Enabled: true,
                    IsBase: false,
                    Error: exception.Message));
            }
        }

        return loaded;
    }

    public static void SetPackEnabled(LoadedContentPack pack, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (pack.IsBase)
        {
            throw new InvalidOperationException("The base content pack cannot be disabled.");
        }

        string manifestPath = Path.Combine(pack.Directory, "pack.json");
        JsonObject manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject()
                              ?? throw new InvalidDataException("The content pack manifest is empty.");
        manifest["enabled"] = enabled;
        File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static int RegisterDocument(BlockRegistry registry, BlockPackDocument document, bool forceAll)
    {
        if (document.FormatVersion != 1)
        {
            throw new InvalidDataException($"Unsupported block content format {document.FormatVersion}; expected 1.");
        }

        foreach (BlockContentDefinition block in document.Blocks)
        {
            registry.Register(new BlockDefinition(
                block.Key,
                block.DisplayName,
                ToVector(block.Albedo, nameof(block.Albedo)),
                block.Roughness,
                block.Metallic,
                ToVector(block.Emission, nameof(block.Emission)),
                block.IsCollidable,
                block.IsFluid,
                block.IsReplaceable,
                block.IsPlaceable,
                block.Id,
                block.SurfacePattern,
                block.HardnessSeconds,
                block.IsUnbreakable,
                block.DropItemKey,
                block.PreferredToolType,
                block.RequiredToolTier,
                block.AffectedByGravity,
                block.IsTransparent,
                block.IndexOfRefraction,
                block.Transmission,
                block.Category,
                block.Tags,
                block.SoundSet,
                block.ParticleEffect,
                block.AnimationSpeed,
                block.TextureGenerator,
                block.TextureVariation,
                block.TextureSeed),
                force: forceAll || block.Force);
        }
        return document.Blocks.Count;
    }

    private static BlockPackDocument ReadBlockDocument(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Required block content file was not found. Rebuild or restore '{path}'.",
                path);
        }

        try
        {
            return JsonSerializer.Deserialize<BlockPackDocument>(File.ReadAllText(path), JsonOptions())
                   ?? throw new InvalidDataException("Block content document is empty.");
        }
        catch (Exception exception) when (exception is not FileNotFoundException)
        {
            throw new InvalidDataException($"Could not load block content '{path}': {exception.Message}", exception);
        }
    }

    private static Vector3 ToVector(float[] value, string property)
    {
        if (value.Length != 3)
        {
            throw new InvalidDataException($"{property} must contain exactly three numbers.");
        }
        return new Vector3(value[0], value[1], value[2]);
    }

    private static JsonSerializerOptions JsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class BlockPackDocument
    {
        public int FormatVersion { get; set; } = 1;
        public string Id { get; set; } = "unnamed.content";
        public string Name { get; set; } = "Unnamed content";
        public string Version { get; set; } = "1.0.0";
        public List<BlockContentDefinition> Blocks { get; set; } = new();
    }

    private sealed class BlockContentDefinition
    {
        public ushort Id { get; set; }
        public string Key { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public float[] Albedo { get; set; } = new float[3];
        public float Roughness { get; set; } = 0.8f;
        public float Metallic { get; set; }
        public float[] Emission { get; set; } = new float[3];
        public bool IsCollidable { get; set; } = true;
        public bool IsFluid { get; set; }
        public bool IsReplaceable { get; set; }
        public bool IsPlaceable { get; set; } = true;
        public BlockSurfacePattern SurfacePattern { get; set; }
        public float HardnessSeconds { get; set; } = 0.75f;
        public bool IsUnbreakable { get; set; }
        public string? DropItemKey { get; set; }
        public string? PreferredToolType { get; set; }
        public int RequiredToolTier { get; set; }
        public bool AffectedByGravity { get; set; }
        public bool IsTransparent { get; set; }
        public float IndexOfRefraction { get; set; } = 1.5f;
        public float Transmission { get; set; }
        public string Category { get; set; } = "General";
        public string[] Tags { get; set; } = Array.Empty<string>();
        public string SoundSet { get; set; } = "stone";
        public string ParticleEffect { get; set; } = "dust";
        public float AnimationSpeed { get; set; }
        public string TextureGenerator { get; set; } = "auto";
        public float TextureVariation { get; set; } = -1f;
        public int TextureSeed { get; set; }
        public bool Force { get; set; }
    }

    private sealed class PackManifest
    {
        public string Id { get; set; } = "unnamed.pack";
        public string Name { get; set; } = "Unnamed pack";
        public string Version { get; set; } = "1.0.0";
        public int Priority { get; set; }
        public bool Enabled { get; set; } = true;
        public bool ForceOverrides { get; set; }
    }
}
