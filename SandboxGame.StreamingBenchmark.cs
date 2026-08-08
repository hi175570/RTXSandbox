using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using RTXSandbox.Configuration;
using RTXSandbox.Infrastructure;
using RTXSandbox.Persistence;
using RTXSandbox.Rendering;
using RTXSandbox.Rendering.Backends;
using RTXSandbox.World;
using NumericsVector3 = System.Numerics.Vector3;

namespace RTXSandbox;

public sealed partial class SandboxGame
{
    private const int StreamingBenchmarkSeed = 20260805;
    private const int StreamingBenchmarkSoakOperations = 10_000;
    private const int StreamingBenchmarkSoakOperationsPerFrame = 64;
    private bool _streamingBenchmark;
    private StreamingBenchmarkRun? _streamingBenchmarkRun;
    private WorldSaveService.IncrementalSaveOperation? _benchmarkSaveOperation;
    private string _streamingBenchmarkProfile = "normal";
    private string? _streamingBenchmarkWorldName;
    private string? _streamingBenchmarkWorldDirectory;
    private string? _benchmarkScratchDirectory;
    private bool _streamingBenchmarkStorageCleaned;
    private long _benchmarkUpdateStarted;
    private long _benchmarkRenderStarted;

    private static string StreamingBenchmarkWorldRoot =>
        Path.Combine(RuntimePaths.OutputRoot, "Benchmarks", "Worlds");

    private WorldSaveService CreateStreamingBenchmarkSaveService(BlockRegistry blocks) =>
        new(
            blocks,
            preferredSeed: StreamingBenchmarkSeed,
            worldName: _streamingBenchmarkWorldName ??
                       throw new InvalidOperationException("Benchmark storage was not prepared."),
            saveRoot: StreamingBenchmarkWorldRoot);

    private static void ConfigureStreamingBenchmarkQuality(GameSettings settings)
    {
        settings.VSync = false;
        settings.FpsLimit = 360;
        settings.RenderScale = 1f;
        settings.ClassicLightingQuality = 2;
        settings.ClassicSunlightStrength = 2f;
        settings.ClassicAmbientStrength = 2f;
        settings.ClassicColorSaturation = 1.5f;
        settings.ClassicWaterQuality = 3;
        settings.ClassicWaterWaveStrength = 2f;
        settings.ClassicTransparencyQuality = 3;
        settings.ClassicFogDensity = 1f;
        settings.PbrSpecularQuality = 3;
        settings.PbrReflectionQuality = 3;
        settings.ApplyForwardQualityPreset(ForwardQualityPreset.Ultra);
        settings.DeferredSpecularQuality = 3;
        settings.DeferredReflectionQuality = 3;
        settings.DeferredRendererQuality = 3;
        settings.DeferredGBufferQuality = 3;
        settings.DeferredLightingQuality = 3;
        settings.DeferredShadowQuality = 3;
        settings.DeferredAmbientOcclusionQuality = 3;
        settings.DeferredWaterQuality = 3;
        settings.DeferredTransparencyQuality = 3;
        settings.DeferredFogQuality = 3;
        settings.DeferredPostProcessingQuality = 3;
        settings.DeferredHdr = true;
        settings.DeferredBloom = true;
        settings.HybridLightingQuality = 3;
        settings.HybridSpecularQuality = 3;
        settings.HybridShadowQuality = 3;
        settings.HybridShadowResolution = 4096;
        settings.HybridReflectionQuality = 3;
        settings.HybridAmbientOcclusionQuality = 3;
        settings.HybridWaterQuality = 3;
        settings.HybridTransparencyQuality = 3;
        settings.HybridFogQuality = 3;
        settings.HybridBloom = true;
        settings.PathBounces = 50;
        settings.PathSamplesPerFrame = 8;
        settings.PathMaxAccumulatedSamples = 16_384;
        settings.PathRayDistance = 220f;
        settings.PathDirectLightingStrength = 2f;
        settings.PathEnvironmentIntensity = 2f;
        settings.PathEmissionIntensity = 2f;
        settings.PathReflectionStrength = 2f;
        settings.PathRussianRouletteStart = 12;
        settings.HardwareRayTracingQuality = 3;
        settings.HardwareRayTracingReflectionBounces = 29;
        settings.HardwareRayTracingRayDistance = 240f;
        settings.HardwareRayTracingShadowStrength = 1f;
        settings.HardwareRayTracingEmissionStrength = 2f;
        settings.HardwareRayTracingReflectionStrength = 2f;
        settings.HardwareRayTracingRenderStyle =
            Environment.GetEnvironmentVariable("RTX_SANDBOX_BENCHMARK_STYLE") == "wax"
                ? HardwareRayTracingRenderStyle.WaxMode
                : HardwareRayTracingRenderStyle.Default;
        if (Enum.TryParse(
                Environment.GetEnvironmentVariable("RTX_SANDBOX_BENCHMARK_INFINITE_MIRROR"),
                ignoreCase: true,
                out InfiniteMirrorQuality infiniteMirrorQuality))
            settings.InfiniteMirrorQuality = infiniteMirrorQuality;
    }

    private void PrepareStreamingBenchmarkStorage()
    {
        Directory.CreateDirectory(StreamingBenchmarkWorldRoot);
        _streamingBenchmarkProfile =
            string.Equals(Environment.GetEnvironmentVariable("RTX_SANDBOX_BENCHMARK_PROFILE"), "optical",
                StringComparison.OrdinalIgnoreCase)
                ? "optical"
                : "normal";
        string renderer = SanitizeBenchmarkToken(
            Environment.GetEnvironmentVariable("RTX_SANDBOX_RENDERER") ?? "renderer");
        string runId = SanitizeBenchmarkToken(
            Environment.GetEnvironmentVariable("RTX_SANDBOX_BENCHMARK_RUN_ID") ??
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        _streamingBenchmarkWorldName = $"adversarial-{_streamingBenchmarkProfile}-{renderer}-{runId}";
        _streamingBenchmarkWorldDirectory =
            Path.Combine(StreamingBenchmarkWorldRoot, _streamingBenchmarkWorldName);
        _streamingBenchmarkStorageCleaned = false;
        CleanupAbandonedBenchmarkDirectories(StreamingBenchmarkWorldRoot, "adversarial-", _streamingBenchmarkWorldDirectory);
        string ioRoot = Path.Combine(RuntimePaths.OutputRoot, "Benchmarks", "IO");
        Directory.CreateDirectory(ioRoot);
        CleanupAbandonedBenchmarkDirectories(ioRoot, "scratch-adversarial-", currentDirectory: null);
        Directory.CreateDirectory(_streamingBenchmarkWorldDirectory);
        File.WriteAllText(Path.Combine(_streamingBenchmarkWorldDirectory, ".benchmark-owner"), Environment.ProcessId.ToString());
        // V1 was created only by the benchmark command. Keep normal Saves completely
        // outside this cleanup boundary and remove only the superseded, known test id.
        DeleteConfirmedBenchmarkDirectory(
            Path.Combine(StreamingBenchmarkWorldRoot, "streaming-benchmark-v1"),
            StreamingBenchmarkWorldRoot);
        // V3 adds the deterministic 10,000-operation scenario. V2 used the same
        // burst coordinates without a reset contract and may contain interrupted-run
        // chunks, so retire that known benchmark fixture once while preserving V3.
        DeleteConfirmedBenchmarkDirectory(
            Path.Combine(StreamingBenchmarkWorldRoot, "streaming-benchmark-v2"),
            StreamingBenchmarkWorldRoot);
        // V4 refreshes the reusable fixture after adding the physical-structure stage
        // and collider residency contract. Retire only the known V3 benchmark save;
        // subsequent V4 runs keep reusing the same immutable baseline.
        DeleteConfirmedBenchmarkDirectory(
            Path.Combine(StreamingBenchmarkWorldRoot, "streaming-benchmark-v3"),
            StreamingBenchmarkWorldRoot);
        // V4 exposed that an unfocused benchmark could still receive a foreground
        // game's F5 key and overwrite its own baseline. V5 is read-only at runtime.
        DeleteConfirmedBenchmarkDirectory(
            Path.Combine(StreamingBenchmarkWorldRoot, "streaming-benchmark-v4"),
            StreamingBenchmarkWorldRoot);
        // V5 was the final reusable fixture. New adversarial runs deliberately create
        // a fresh deterministic world and remove it after the report is committed.
        DeleteConfirmedBenchmarkDirectory(
            Path.Combine(StreamingBenchmarkWorldRoot, "streaming-benchmark-v5"),
            StreamingBenchmarkWorldRoot);
    }

    private static string SanitizeBenchmarkToken(string value)
    {
        string sanitized = new(value
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_')
            .Take(20)
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "run" : sanitized;
    }

    private static void CleanupAbandonedBenchmarkDirectories(
        string root,
        string prefix,
        string? currentDirectory)
    {
        if (!Directory.Exists(root)) return;
        string? current = currentDirectory is null ? null : Path.GetFullPath(currentDirectory);
        foreach (string directory in Directory.EnumerateDirectories(root, prefix + "*"))
        {
            string fullDirectory = Path.GetFullPath(directory);
            if (current is not null && string.Equals(fullDirectory, current, StringComparison.OrdinalIgnoreCase))
                continue;
            string ownerPath = Path.Combine(fullDirectory, ".benchmark-owner");
            if (File.Exists(ownerPath) && int.TryParse(File.ReadAllText(ownerPath), out int processId))
            {
                try
                {
                    using Process owner = Process.GetProcessById(processId);
                    if (!owner.HasExited) continue;
                }
                catch (ArgumentException)
                {
                    // The owner no longer exists; this directory is an interrupted run.
                }
            }
            DeleteConfirmedBenchmarkDirectory(fullDirectory, root);
            Console.WriteLine($"Removed abandoned benchmark directory: {Path.GetFileName(fullDirectory)}");
        }
    }

    private static void DeleteConfirmedBenchmarkDirectory(string candidate, string allowedRoot)
    {
        string root = Path.GetFullPath(allowedRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullCandidate = Path.GetFullPath(candidate);
        if (!fullCandidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fullCandidate.TrimEnd(Path.DirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to clean benchmark path outside {root}: {fullCandidate}");
        if (Directory.Exists(fullCandidate)) Directory.Delete(fullCandidate, recursive: true);
    }

    private static NumericsVector3 StreamingBenchmarkStartPosition(VoxelWorld world)
    {
        const int startX = 8;
        const int startZ = 8;
        float y = world.FindTopVoxelY(startX, startZ) + 2.001f;
        return new NumericsVector3(startX + 0.5f, y, startZ + 0.5f);
    }

    private void BuildStreamingBenchmarkBaseline(VoxelWorld world)
    {
        world.Regenerate(WorldGenerationSettings.Natural(StreamingBenchmarkSeed));
        for (int chunkZ = -1; chunkZ <= 1; chunkZ++)
        for (int chunkX = -1; chunkX <= 1; chunkX++)
            world.EnsureChunkGenerated(new ChunkCoordinate(chunkX, chunkZ));

        int platformY = int.MinValue;
        for (int z = 2; z <= 29; z++)
        for (int x = 2; x <= 29; x++)
            platformY = Math.Max(platformY, world.FindTopVoxelY(x, z));
        platformY = Math.Clamp(platformY + 2, VoxelWorld.MinimumY + 4, 240);

        for (int z = 2; z <= 29; z++)
        for (int x = 2; x <= 29; x++)
            world.SetBlockId(x, platformY, z,
                (ushort)(((x + z) & 7) == 0 ? VoxelMaterial.Mirror :
                    ((x + z) & 3) == 0 ? VoxelMaterial.Marble : VoxelMaterial.WhiteConcrete));

        // Water and transparent materials share a fixed basin so every renderer sees
        // identical alpha/refraction geometry.
        for (int z = 3; z <= 15; z++)
        for (int x = 3; x <= 18; x++)
            world.SetBlockId(x, platformY + 1, z, (ushort)VoxelMaterial.Water);
        for (int i = 2; i <= 18; i++)
        {
            world.SetBlockId(i, platformY + 1, 2, (ushort)VoxelMaterial.Glass);
            world.SetBlockId(19, platformY + 1, i, (ushort)VoxelMaterial.FrostedGlass);
        }

        for (int y = platformY + 1; y <= platformY + 8; y++)
        for (int x = 21; x <= 29; x++)
            world.SetBlockId(x, y, 29, (ushort)VoxelMaterial.Mirror);

        VoxelMaterial[] lamps =
            { VoxelMaterial.Glow, VoxelMaterial.RedLamp, VoxelMaterial.BlueLamp, VoxelMaterial.PurpleLamp };
        for (int x = 3; x <= 27; x += 3)
            world.SetBlockId(x, platformY + 1, 22, (ushort)lamps[(x / 3) % lamps.Length]);

        var micro = new MicroVoxelContainer(4);
        micro.Fill((ushort)VoxelMaterial.Copper);
        micro.PaintCell(4, 0, 0, 0, 0);
        micro.PaintCell(4, 3, 3, 3, (ushort)VoxelMaterial.Glow);
        world.SetMicroVoxels(10, platformY + 1, 5, micro);

        var elements = new List<PhysicsGridElement>();
        for (int z = 0; z < 10; z++)
        for (int x = 0; x < 12; x++)
        {
            ushort material = (ushort)(((x + z) & 3) == 0 ? VoxelMaterial.Glow : VoxelMaterial.Metal);
            elements.Add(new PhysicsGridElement(
                x, 0, z,
                new NumericsVector3(x, 0, z),
                new NumericsVector3(x + 1, 1, z + 1),
                material));
        }
        PhysicsGrid grid = world.PhysicsGrids.Create(
            elements,
            new NumericsVector3(7, platformY + 7, 18));
        grid.AngularVelocity = new NumericsVector3(0f, 0.18f, 0f);

        var secondGridElements = new List<PhysicsGridElement>();
        for (int y = 0; y < 6; y++)
        for (int x = 0; x < 8; x++)
            secondGridElements.Add(new PhysicsGridElement(
                x, y, 0,
                new NumericsVector3(x, y, 0),
                new NumericsVector3(x + 1, y + 1, 1),
                (ushort)(y % 3 == 0 ? VoxelMaterial.Copper : VoxelMaterial.Metal)));
        PhysicsGrid secondGrid = world.PhysicsGrids.Create(
            secondGridElements,
            new NumericsVector3(20, platformY + 4, 20));
        secondGrid.AngularVelocity = new NumericsVector3(0.08f, -0.12f, 0.04f);

        if (_streamingBenchmarkProfile == "optical")
            BuildOpticalBenchmarkGallery(world, platformY);
    }

    private void BuildOpticalBenchmarkGallery(VoxelWorld world, int platformY)
    {
        ushort[] glass = GetOpticalBenchmarkMaterials();

        // Dense overlapping walls make refraction, rough transmission and recursive
        // reflections expensive without changing renderer quality settings.
        for (int layer = 0; layer < glass.Length; layer++)
        {
            int x = 34 + layer * 3;
            for (int y = 1; y <= 10; y++)
            for (int z = 2; z <= 29; z++)
                world.SetBlockId(x, platformY + y, z, glass[layer]);
        }
        for (int z = 34; z <= 61; z++)
        for (int x = 2; x <= 61; x++)
        {
            ushort material = ((x + z) & 3) switch
            {
                0 => (ushort)VoxelMaterial.Mirror,
                1 => (ushort)VoxelMaterial.Metal,
                2 => (ushort)VoxelMaterial.Copper,
                _ => glass[(x + z) % glass.Length],
            };
            world.SetBlockId(x, platformY, z, material);
            if ((x + z) % 11 == 0)
                world.SetBlockId(x, platformY + 1, z, (ushort)VoxelMaterial.Glow);
        }

        var opticalGridElements = new List<PhysicsGridElement>();
        for (int z = 0; z < 12; z++)
        for (int x = 0; x < 12; x++)
            opticalGridElements.Add(new PhysicsGridElement(
                x, 0, z,
                new NumericsVector3(x, 0, z),
                new NumericsVector3(x + 1, 1, z + 1),
                (x + z) % 5 == 0 ? (ushort)VoxelMaterial.Mirror : glass[(x + z) % glass.Length]));
        PhysicsGrid opticalGrid = world.PhysicsGrids.Create(
            opticalGridElements,
            new NumericsVector3(37, platformY + 13, 38));
        opticalGrid.AngularVelocity = new NumericsVector3(0.11f, -0.16f, 0.07f);
    }

    private ushort[] GetOpticalBenchmarkMaterials()
    {
        string[] keys =
        {
            "rtxsandbox:glass", "rtxsandbox:blue_glass", "rtxsandbox:red_glass",
            "rtxsandbox:green_glass", "rtxsandbox:yellow_glass", "rtxsandbox:purple_glass",
            "rtxsandbox:smoked_glass", "rtxsandbox:diamond_glass", "rtxsandbox:prismatic_glass",
            "rtxsandbox:frosted_glass", "rtxsandbox:matte_glass", "rtxsandbox:mosaic_glass",
            "rtxsandbox:acrylic_glass", "rtxsandbox:ribbed_glass", "rtxsandbox:etched_glass",
        };
        return keys.Select(key =>
        {
            if (!_blocks.TryGet(key, out var definition) || definition is null)
                throw new InvalidOperationException($"Optical benchmark material is missing: {key}");
            return definition.Id;
        }).ToArray();
    }

    private void InitializeStreamingBenchmark(bool baselineCreated)
    {
        _world.ResetChunkPerformanceCounters();
        _physics.ResetPerformanceCounters();
        if (_renderer.ActiveBackend is RasterVoxelRenderer raster) raster.ResetPerformanceCounters();
        if (_renderer.ActiveBackend is DxrHardwareRenderer dxr) dxr.ResetPerformanceCounters();
        if (_renderer.ActiveBackend is RayTracingRenderer compute) compute.ResetPerformanceCounters();
        _streamingBenchmarkRun = new StreamingBenchmarkRun(
            _renderer.Kind,
            Environment.GetEnvironmentVariable("RTX_SANDBOX_BENCHMARK_LABEL") ?? "current",
            _streamingBenchmarkProfile,
            _streamingBenchmarkWorldName ?? "unknown",
            _settings.HardwareRayTracingRenderStyle.ToString(),
            baselineCreated,
            _worldSaves.LastLoadMilliseconds);
        Console.WriteLine($"Streaming benchmark ready: ephemeral world '{_streamingBenchmarkWorldName}', " +
                          $"profile {_streamingBenchmarkProfile}, renderer {_renderer.Kind}, " +
                          $"baseline {(baselineCreated ? "created" : "unexpectedly reloaded")}, " +
                          $"region read {_worldSaves.LastLoadMilliseconds:0.00} ms.");
    }

    private void RecordStreamingBenchmarkUpdateStart()
    {
        if (_streamingBenchmark) _benchmarkUpdateStarted = Stopwatch.GetTimestamp();
    }

    private void RecordStreamingBenchmarkUpdateEnd(int submittedChunks)
    {
        if (!_streamingBenchmark || _streamingBenchmarkRun is null) return;
        double elapsed = BenchmarkElapsedMilliseconds(_benchmarkUpdateStarted);
        _streamingBenchmarkRun.RecordUpdate(elapsed, submittedChunks, CurrentPendingBenchmarkJobs());
    }

    private void RecordStreamingBenchmarkRenderStart()
    {
        if (_streamingBenchmark) _benchmarkRenderStarted = Stopwatch.GetTimestamp();
    }

    private void RecordStreamingBenchmarkRenderEnd(double frameDeltaMilliseconds)
    {
        if (!_streamingBenchmark || _streamingBenchmarkRun is null) return;
        double renderMilliseconds = BenchmarkElapsedMilliseconds(_benchmarkRenderStarted);
        _streamingBenchmarkRun.RecordFrame(
            frameDeltaMilliseconds,
            renderMilliseconds,
            CurrentPendingBenchmarkJobs(),
            _world.CompletedChunkGenerationPendingCommitCount,
            _world.LoadedChunkCount);
        long controllerStarted = Stopwatch.GetTimestamp();
        AdvanceStreamingBenchmark();
        _streamingBenchmarkRun.RecordControllerWork(BenchmarkElapsedMilliseconds(controllerStarted));
    }

    private int CurrentPendingBenchmarkJobs()
    {
        int pending = _world.PendingChunkGenerationCount + _physics.PendingColliderBuildCount +
                      _physics.PendingStructuralEvaluationCount;
        if (_renderer.ActiveBackend is RasterVoxelRenderer raster)
            pending += raster.PendingChunkMeshJobCount + raster.PendingVisibleMeshRebuildCount;
        if (_renderer.ActiveBackend is DxrHardwareRenderer dxr)
            pending += dxr.PendingStaticGeometryBuildCount;
        if (_renderer.ActiveBackend is RayTracingRenderer compute)
            pending += compute.PendingVoxelUploadCount;
        return pending;
    }

    private bool StreamingBenchmarkWorkReady()
    {
        if (_world.PendingChunkGenerationCount != 0 || _physics.PendingColliderBuildCount != 0 ||
            _physics.PendingStructuralEvaluationCount != 0) return false;
        if (_renderer.ActiveBackend is DxrHardwareRenderer dxr && dxr.PendingStaticGeometryBuildCount != 0)
            return false;
        if (_renderer.ActiveBackend is RayTracingRenderer compute && compute.PendingVoxelUploadCount != 0)
            return false;
        return _renderer.ActiveBackend is not RasterVoxelRenderer raster ||
               (raster.PendingChunkMeshJobCount == 0 && raster.PendingVisibleMeshRebuildCount == 0);
    }

    private void AdvanceStreamingBenchmark()
    {
        StreamingBenchmarkRun run = _streamingBenchmarkRun!;
        if (run.Stage == StreamingBenchmarkStage.RegionWrite)
        {
            if (run.RegionWriteCompleted)
            {
                bool readyAfterSave = StreamingBenchmarkWorkReady();
                int completedAsBuildCount = _renderer.ActiveBackend is DxrHardwareRenderer completedDxr
                    ? completedDxr.AccelerationStructureBuildCount
                    : 0;
                if (!run.WaitForStableWork(readyAfterSave, completedAsBuildCount)) return;
                FinishStreamingBenchmark(run);
                return;
            }
            if (_benchmarkSaveOperation is null)
                throw new InvalidOperationException("Benchmark Region write stage has no save operation.");
            if (!_benchmarkSaveOperation.SnapshotComplete)
            {
                _benchmarkSaveOperation.CaptureNextChunks(
                    FrameWorkBudget.Default.MaximumSaveSnapshotsPerFrame);
                return;
            }
            if (_benchmarkSaveOperation.WriteTask is null)
            {
                _benchmarkSaveOperation.StartBackgroundWrite();
                return;
            }
            if (!_benchmarkSaveOperation.IsCompleted) return;
            _benchmarkSaveOperation.ThrowIfFailed();
            run.RegionSnapshotTotalMilliseconds = _benchmarkSaveOperation.SnapshotTotalMilliseconds;
            run.RegionSnapshotMaximumStepMilliseconds = _benchmarkSaveOperation.MaximumSnapshotStepMilliseconds;
            run.RegionWriteMilliseconds = _benchmarkSaveOperation.WriteMilliseconds;
            run.RegionWriteCompleted = true;
            run.RestartStageWait();
            return;
        }
        if (run.Stage == StreamingBenchmarkStage.OperationSoak10000 &&
            run.SoakOperationIndex < StreamingBenchmarkSoakOperations)
        {
            ApplyBenchmarkSoakBatch(run);
            return;
        }
        if (run.Stage == StreamingBenchmarkStage.OpticalChunkPayload5120 &&
            run.OpticalOperationIndex < StreamingBenchmarkRun.OpticalOperationCount)
        {
            ApplyOpticalChunkPayloadBatch(run);
            return;
        }
        if (run.Stage == StreamingBenchmarkStage.Place100 && run.PlaceOperationIndex < 100)
        {
            ApplyBenchmarkBlockChangeBatch(run, place: true);
            return;
        }
        if (run.Stage == StreamingBenchmarkStage.Break100 && run.BreakOperationIndex < 100)
        {
            ApplyBenchmarkBlockChangeBatch(run, place: false);
            return;
        }
        bool ready = StreamingBenchmarkWorkReady();
        int asBuildCount = _renderer.ActiveBackend is DxrHardwareRenderer dxr
            ? dxr.AccelerationStructureBuildCount
            : 0;
        run.LogPendingIfSlow(
            ready,
            _world.PendingChunkGenerationCount,
            _world.CompletedChunkGenerationPendingCommitCount,
            _physics.PendingColliderBuildCount,
            _renderer.ActiveBackend is RasterVoxelRenderer pendingRaster
                ? pendingRaster.PendingChunkMeshJobCount + pendingRaster.PendingVisibleMeshRebuildCount
                : 0,
            _renderer.ActiveBackend is DxrHardwareRenderer pendingDxr
                ? pendingDxr.PendingStaticGeometryBuildCount
                : 0);
        if (!run.WaitForStableWork(ready, asBuildCount)) return;

        switch (run.Stage)
        {
            case StreamingBenchmarkStage.Warmup:
                StartChunkBurst(
                    run,
                    20,
                    centerChunkX: _streamingBenchmarkProfile == "optical" ? 10 : 6,
                    measured: false);
                break;
            case StreamingBenchmarkStage.WarmupBurst20:
                ResetStreamingBenchmarkMeasuredCounters();
                StartChunkBurst(run, 1, centerChunkX: 16);
                break;
            case StreamingBenchmarkStage.Burst1:
                StartChunkBurst(run, 5, centerChunkX: 22);
                break;
            case StreamingBenchmarkStage.Burst5:
                StartChunkBurst(run, 10, centerChunkX: 30);
                break;
            case StreamingBenchmarkStage.Burst10:
                StartChunkBurst(run, 20, centerChunkX: 40);
                break;
            case StreamingBenchmarkStage.Burst20:
                StartChunkBurst(run, 50, centerChunkX: 54);
                break;
            case StreamingBenchmarkStage.Burst50:
                StartChunkBurst(run, 100, centerChunkX: 72);
                break;
            case StreamingBenchmarkStage.Burst100:
                if (_streamingBenchmarkProfile == "optical")
                    run.BeginStage(StreamingBenchmarkStage.OpticalChunkPayload5120);
                else
                    BeginBoundaryRoute(run);
                break;
            case StreamingBenchmarkStage.OpticalChunkPayload5120:
                BeginBoundaryRoute(run);
                break;
            case StreamingBenchmarkStage.BoundaryRoute:
                if (run.BoundaryRouteIndex < 24) QueueNextBoundaryRouteChunk(run);
                else if (run.CurrentStageElapsedMilliseconds < 10_000.0) run.RestartStageWait();
                else
                {
                    MoveToBenchmarkModificationZone();
                    run.BeginStage(StreamingBenchmarkStage.Place100);
                    PrepareBenchmarkBlockChanges(run);
                }
                break;
            case StreamingBenchmarkStage.Place100:
                run.BeginStage(StreamingBenchmarkStage.Break100);
                run.RestartStageWait();
                break;
            case StreamingBenchmarkStage.Break100:
                run.BeginStage(StreamingBenchmarkStage.MaterialAndGridEdits);
                ApplyBenchmarkMaterialAndGridChanges();
                break;
            case StreamingBenchmarkStage.MaterialAndGridEdits:
                run.BeginStage(StreamingBenchmarkStage.PhysicsStructure256);
                ApplyBenchmarkPhysicsStructure(run);
                run.RestartStageWait();
                break;
            case StreamingBenchmarkStage.PhysicsStructure256:
                if (_physics.ActiveGridCount <= run.PhysicsGridCountBefore)
                    throw new InvalidOperationException("256-block stress structure did not become a Physics Grid.");
                run.BeginStage(StreamingBenchmarkStage.OperationSoak10000);
                run.RestartStageWait();
                break;
            case StreamingBenchmarkStage.OperationSoak10000:
                run.BeginStage(StreamingBenchmarkStage.RegionWrite);
                SaveStreamingBenchmarkScratch(run);
                break;
        }
    }

    private void ResetStreamingBenchmarkMeasuredCounters()
    {
        _world.ResetChunkPerformanceCounters();
        _physics.ResetPerformanceCounters();
        if (_renderer.ActiveBackend is RasterVoxelRenderer raster) raster.ResetPerformanceCounters();
        if (_renderer.ActiveBackend is DxrHardwareRenderer dxr) dxr.ResetPerformanceCounters();
        if (_renderer.ActiveBackend is RayTracingRenderer compute) compute.ResetPerformanceCounters();
        _streamingBenchmarkRun!.ResetMeasuredFrames();
    }

    private void StartChunkBurst(
        StreamingBenchmarkRun run,
        int count,
        int centerChunkX,
        bool measured = true)
    {
        ChunkCoordinate[] chunks = CreateBenchmarkBurst(centerChunkX, count);
        _player.Teleport(new NumericsVector3(
            centerChunkX * ChunkCoordinate.Size + ChunkCoordinate.Size * 0.5f,
            112f,
            ChunkCoordinate.Size * 0.5f));
        _renderer.ResetAccumulation();
        int queued = _world.QueueChunkBurst(chunks);
        if (queued != count)
            throw new InvalidOperationException($"Benchmark requested {count} fresh chunks but queued {queued}.");
        if (measured) run.RecordOperations(count, queued);
        run.BeginStage(!measured ? StreamingBenchmarkStage.WarmupBurst20 : count switch
        {
            1 => StreamingBenchmarkStage.Burst1,
            5 => StreamingBenchmarkStage.Burst5,
            10 => StreamingBenchmarkStage.Burst10,
            20 => StreamingBenchmarkStage.Burst20,
            50 => StreamingBenchmarkStage.Burst50,
            100 => StreamingBenchmarkStage.Burst100,
            _ => throw new ArgumentOutOfRangeException(nameof(count)),
        });
        if (measured) run.RecordBurst(count);
        Console.WriteLine($"Benchmark {(measured ? "measured" : "warm-up")} burst queued exactly {count} chunks at X={centerChunkX}.");
    }

    private static ChunkCoordinate[] CreateBenchmarkBurst(int centerChunkX, int count)
    {
        int diameter = (int)Math.Ceiling(Math.Sqrt(count));
        if ((diameter & 1) == 0) diameter++;
        int radius = diameter / 2;
        return Enumerable.Range(-radius, diameter)
            .SelectMany(z => Enumerable.Range(-radius, diameter)
                .Select(x => new ChunkCoordinate(centerChunkX + x, z)))
            .OrderBy(chunk => Math.Abs(chunk.X - centerChunkX) + Math.Abs(chunk.Z))
            .ThenBy(chunk => chunk.Z)
            .ThenBy(chunk => chunk.X)
            .Take(count)
            .ToArray();
    }

    private void QueueNextBoundaryRouteChunk(StreamingBenchmarkRun run)
    {
        int chunkX = 86 + run.BoundaryRouteIndex;
        var chunk = new ChunkCoordinate(chunkX, run.BoundaryRouteIndex % 2);
        _player.Teleport(new NumericsVector3(
            chunk.MinimumX + 1.25f,
            104f,
            chunk.MinimumZ + ChunkCoordinate.Size * 0.5f));
        _renderer.ResetAccumulation();
        int queued = _world.QueueChunkBurst(new[] { chunk });
        if (queued != 1)
            throw new InvalidOperationException($"Boundary route chunk {chunk} was not fresh.");
        run.RecordOperations(1, 1);
        run.BoundaryRouteIndex++;
        run.RestartStageWait();
    }

    private void BeginBoundaryRoute(StreamingBenchmarkRun run)
    {
        run.BeginStage(StreamingBenchmarkStage.BoundaryRoute);
        QueueNextBoundaryRouteChunk(run);
    }

    private void ApplyOpticalChunkPayloadBatch(StreamingBenchmarkRun run)
    {
        const int operationsPerFrame = 64;
        ushort[] materials = run.OpticalMaterials ??= GetOpticalBenchmarkMaterials();
        int start = run.OpticalOperationIndex;
        int end = Math.Min(StreamingBenchmarkRun.OpticalOperationCount, start + operationsPerFrame);
        for (int operation = start; operation < end; operation++)
        {
            int chunkIndex = operation / (ChunkCoordinate.Size * ChunkCoordinate.Size);
            int local = operation % (ChunkCoordinate.Size * ChunkCoordinate.Size);
            ChunkCoordinate chunk = run.SoakChunks[chunkIndex];
            int x = chunk.MinimumX + local % ChunkCoordinate.Size;
            int z = chunk.MinimumZ + local / ChunkCoordinate.Size;
            int y = Math.Min(_world.FindTopVoxelY(x, z) + 1, VoxelWorld.MaximumY - 1);
            ushort material = materials[operation % materials.Length];
            if (!_world.SetBlockId(x, y, z, material))
                throw new InvalidOperationException(
                    $"Optical payload produced no change at ({x},{y},{z}).");
        }
        int applied = end - start;
        run.OpticalOperationIndex = end;
        run.RecordOperations(applied, applied);
        _renderer.ResetAccumulation();
        run.RestartStageWait();
    }

    private void PrepareBenchmarkBlockChanges(StreamingBenchmarkRun run)
    {
        const int boundaryX = 72 * ChunkCoordinate.Size + ChunkCoordinate.Size - 5;
        const int boundaryZ = -5;
        run.ModificationPoints.Clear();
        run.PlaceOperationIndex = 0;
        run.BreakOperationIndex = 0;
        for (int offset = 0; offset < 1_024 && run.ModificationPoints.Count < 100; offset++)
        {
            int x = boundaryX + offset % 32;
            int z = boundaryZ + offset / 32;
            int supportY = _world.FindTopVoxelY(x, z);
            while (supportY >= VoxelWorld.MinimumY && !_world.IsSolidForCollision(x, supportY, z))
                supportY--;
            int y = supportY + 1;
            if (y >= VoxelWorld.MaximumY || _world.GetBlockId(x, y, z) != 0 ||
                _world.TryGetMicroVoxels(x, y, z, out _)) continue;
            run.ModificationPoints.Add((x, y, z));
        }
        if (run.ModificationPoints.Count != 100)
            throw new InvalidOperationException(
                $"Benchmark found only {run.ModificationPoints.Count}/100 supported placement cells.");
    }

    private void ApplyBenchmarkBlockChangeBatch(StreamingBenchmarkRun run, bool place)
    {
        const int changesPerFrame = 1;
        int start = place ? run.PlaceOperationIndex : run.BreakOperationIndex;
        int end = Math.Min(100, start + changesPerFrame);
        ushort material = place ? (ushort)VoxelMaterial.WhiteConcrete : (ushort)VoxelMaterial.Air;
        for (int index = start; index < end; index++)
        {
            (int x, int y, int z) = run.ModificationPoints[index];
            if (!_world.SetBlockId(x, y, z, material))
                throw new InvalidOperationException(
                    $"Supported {(place ? "placement" : "break")} failed at ({x},{y},{z}).");
        }
        run.RecordOperations(end - start, end - start);
        if (place) run.PlaceOperationIndex = end;
        else run.BreakOperationIndex = end;
        _renderer.ResetAccumulation();
        run.RestartStageWait();
    }

    private void MoveToBenchmarkModificationZone()
    {
        const int centerChunkX = 72;
        _player.Teleport(new NumericsVector3(
            centerChunkX * ChunkCoordinate.Size + ChunkCoordinate.Size * 0.5f,
            178f,
            ChunkCoordinate.Size * 0.5f));
        _renderer.ResetAccumulation();
    }

    private void ApplyBenchmarkSoakBatch(StreamingBenchmarkRun run)
    {
        long started = Stopwatch.GetTimestamp();
        int applied = 0;
        while (applied < StreamingBenchmarkSoakOperationsPerFrame &&
               run.SoakOperationIndex < StreamingBenchmarkSoakOperations)
        {
            int operation = run.SoakOperationIndex;
            bool place = operation < StreamingBenchmarkSoakOperations / 2;
            int cell = place ? operation : operation - StreamingBenchmarkSoakOperations / 2;
            int chunkIndex = cell % 100;
            int localCell = cell / 100;
            ChunkCoordinate chunk = run.SoakChunks[chunkIndex];
            int x = chunk.MinimumX + localCell % 10;
            int z = chunk.MinimumZ + localCell / 10;
            // Exercise 100 distinct chunks without spawning 5,000 unsupported
            // falling structures. These cells are inside the generated terrain, so
            // the first half replaces full solid materials and the second half
            // restores their exact original values.
            const int y = VoxelWorld.MinimumY + 1;
            ushort material;
            if (place)
            {
                ushort original = _world.GetBlockId(x, y, z);
                run.SoakOriginalBlockIds[cell] = original;
                material = (ushort)((cell % 4) switch
                {
                    0 => VoxelMaterial.Stone,
                    1 => VoxelMaterial.Marble,
                    2 => VoxelMaterial.Copper,
                    _ => VoxelMaterial.Metal,
                });
                if (material == original)
                    material = material == (ushort)VoxelMaterial.Stone
                        ? (ushort)VoxelMaterial.Marble
                        : (ushort)VoxelMaterial.Stone;
            }
            else
            {
                material = run.SoakOriginalBlockIds[cell];
            }
            if (!_world.SetBlockId(x, y, z, material))
                throw new InvalidOperationException(
                    $"10,000-operation soak produced no change at operation {operation}, cell ({x},{y},{z}).");
            run.SoakOperationIndex++;
            applied++;
        }
        double elapsed = BenchmarkElapsedMilliseconds(started);
        run.RecordOperations(applied, applied);
        run.RecordSoakBatch(applied, elapsed);
        _renderer.ResetAccumulation();
        run.RestartStageWait();
    }

    private void ApplyBenchmarkMaterialAndGridChanges()
    {
        const int baseX = 72 * ChunkCoordinate.Size + 12;
        const int baseZ = 12;
        const int y = 171;
        int completed = 0;
        for (int index = 0; index < 24; index++)
        {
            ushort material = index < 8
                ? (ushort)VoxelMaterial.Water
                : index < 16 ? (ushort)VoxelMaterial.Glass : (ushort)VoxelMaterial.Glow;
            if (_world.SetBlockId(baseX + index % 8, y, baseZ + index / 8, material)) completed++;
        }
        var micro = new MicroVoxelContainer(4);
        micro.Fill((ushort)VoxelMaterial.Copper);
        micro.PaintCell(4, 1, 1, 1, (ushort)VoxelMaterial.Glow);
        if (_world.SetMicroVoxels(baseX + 8, y, baseZ, micro)) completed++;

        PhysicsGrid? grid = _world.PhysicsGrids.Grids.FirstOrDefault();
        if (grid is not null)
        {
            for (int x = 12; x < 20; x++) if (grid.TryAddFullCell(x, 0, 0, (ushort)VoxelMaterial.Metal)) completed++;
            for (int x = 12; x < 16; x++) if (grid.TryRemoveCell(x, 0, 0)) completed++;
            _world.PhysicsGrids.NotifyChanged();
        }
        _streamingBenchmarkRun!.RecordOperations(37, completed);
        _renderer.ResetAccumulation();
        _streamingBenchmarkRun!.RestartStageWait();
    }

    private void ApplyBenchmarkPhysicsStructure(StreamingBenchmarkRun run)
    {
        const int baseX = 72 * ChunkCoordinate.Size + 2;
        const int baseY = 190;
        const int baseZ = 2;
        run.PhysicsGridCountBefore = _physics.ActiveGridCount;
        for (int y = 0; y < 4; y++)
        for (int z = 0; z < 8; z++)
        for (int x = 0; x < 8; x++)
            if (!_world.SetBlockId(baseX + x, baseY + y, baseZ + z, (ushort)VoxelMaterial.WhiteConcrete))
                throw new InvalidOperationException("Physics structure stress setup overlapped existing geometry.");
        run.RecordOperations(256, 256);
        _renderer.ResetAccumulation();
    }

    private void SaveStreamingBenchmarkScratch(StreamingBenchmarkRun run)
    {
        string ioRoot = Path.Combine(RuntimePaths.OutputRoot, "Benchmarks", "IO");
        Directory.CreateDirectory(ioRoot);
        string scratchName = $"scratch-{_streamingBenchmarkWorldName}";
        scratchName = scratchName[..Math.Min(64, scratchName.Length)];
        _benchmarkScratchDirectory = Path.Combine(ioRoot, scratchName);
        DeleteConfirmedBenchmarkDirectory(_benchmarkScratchDirectory, ioRoot);
        Directory.CreateDirectory(_benchmarkScratchDirectory);
        File.WriteAllText(Path.Combine(_benchmarkScratchDirectory, ".benchmark-owner"), Environment.ProcessId.ToString());
        var scratch = new WorldSaveService(
            _blocks,
            preferredSeed: StreamingBenchmarkSeed,
            worldName: scratchName,
            saveRoot: ioRoot);
        _benchmarkSaveOperation = scratch.BeginIncrementalSave(
            _world,
            _player.Position,
            _clock.TimeOfDayHours);
        run.RestartStageWait();
    }

    private void FinishStreamingBenchmark(StreamingBenchmarkRun run)
    {
        if (_renderer.ActiveBackend is RasterVoxelRenderer raster)
        {
            run.MeshBuildCount = raster.ChunkMeshBuildCount;
            run.MeshBuildTotalMilliseconds = raster.ChunkMeshBuildTotalMilliseconds;
            run.MeshBuildMaximumMilliseconds = raster.MaximumChunkMeshBuildMilliseconds;
            run.GpuUploadCount = raster.ChunkGpuUploadCount;
            run.GpuUploadTotalMilliseconds = raster.ChunkGpuUploadTotalMilliseconds;
            run.GpuUploadMaximumMilliseconds = raster.MaximumChunkGpuUploadMilliseconds;
            run.UploadBytes = raster.ChunkGpuUploadBytes;
            run.CpuMeshMemoryBytes = raster.ResidentCpuMeshBytes;
            run.GpuMeshMemoryBytes = raster.ResidentGpuMeshBytes;
            run.ResidentChunkMeshCount = raster.ResidentChunkMeshCount;
            run.EvictedChunkMeshCount = raster.EvictedChunkMeshCount;
            run.ShadowUpdateCount = raster.DirectionalShadowMapUpdateCount;
            run.ShadowUpdateTotalMilliseconds = raster.DeferredShadowUpdateTotalMilliseconds;
            run.ShadowUpdateMaximumMilliseconds = raster.MaximumDeferredShadowUpdateMilliseconds;
            run.ForwardShadowGpuMaximumMilliseconds = raster.MaximumForwardShadowGpuMilliseconds;
            run.ForwardMainGpuMaximumMilliseconds = raster.MaximumForwardMainGpuMilliseconds;
            run.ForwardTransparencyGpuMaximumMilliseconds = raster.MaximumForwardTransparencyGpuMilliseconds;
            run.ForwardPostGpuMaximumMilliseconds = raster.MaximumForwardPostGpuMilliseconds;
        }
        if (_renderer.ActiveBackend is RayTracingRenderer compute)
        {
            run.ComputeUploadCount = compute.VoxelUploadCount;
            run.ComputeUploadTotalMilliseconds = compute.VoxelUploadTotalMilliseconds;
            run.ComputeUploadMaximumMilliseconds = compute.MaximumVoxelUploadMilliseconds;
            run.ComputeUploadBytes = compute.VoxelUploadBytes;
            run.UploadBytes += compute.VoxelUploadBytes;
        }
        if (_renderer.ActiveBackend is DxrHardwareRenderer dxr)
        {
            run.DxrBuildCount = dxr.AccelerationStructureBuildCount;
            run.DxrStaticBuildCount = dxr.StaticAccelerationStructureBuildCount;
            run.DxrDynamicBuildCount = dxr.DynamicAccelerationStructureBuildCount;
            run.DxrTlasUpdateCount = dxr.TlasUpdateCount;
            run.DxrBuildTotalMilliseconds = dxr.AccelerationStructureBuildTotalMilliseconds;
            run.DxrBuildMaximumMilliseconds = dxr.MaximumAccelerationStructureBuildMilliseconds;
            run.DxrGeometryBuildMaximumMilliseconds = dxr.MaximumStaticGeometryBuildMilliseconds;
            run.DxrAccelerationStructureMemoryBytes = dxr.AccelerationStructureMemoryBytes;
            run.DxrSynchronizedTraceMaximumMilliseconds = dxr.MaximumSynchronizedTraceMilliseconds;
            run.DxrGpuTraceMaximumMilliseconds = dxr.MaximumGpuTraceMilliseconds;
            run.ReflectionTailCpuMaximumMilliseconds = dxr.MaximumReflectionTailCpuMilliseconds;
            run.ReflectionTailAtlasMemoryBytes = dxr.ReflectionTailAtlasMemoryBytes;
            run.RealDxrReflectionLayers = dxr.ActualRealReflectionLayers;
            run.ApproximateReflectionLayers = dxr.ApproximateReflectionLayers;
        }
        run.GenerationCount = _world.CompletedChunkGenerationCount;
        run.GenerationTotalMilliseconds = _world.ChunkGenerationTotalMilliseconds;
        run.GenerationMaximumMilliseconds = _world.MaximumChunkGenerationMilliseconds;
        run.ChunkCommitCount = _world.CommittedChunkGenerationCount;
        run.ChunkCommitTotalMilliseconds = _world.ChunkCommitTotalMilliseconds;
        run.ChunkCommitMaximumMilliseconds = _world.MaximumChunkCommitMilliseconds;
        run.ColliderBuildCount = _physics.ColliderBuildCount;
        run.ColliderBuildTotalMilliseconds = _physics.ColliderBuildTotalMilliseconds;
        run.ColliderBuildMaximumMilliseconds = _physics.MaximumColliderBuildMilliseconds;
        run.PhysicsDynamicReconcileMaximumMilliseconds = _physics.MaximumDynamicReconcileMilliseconds;
        run.PhysicsSimulationMaximumMilliseconds = _physics.MaximumSimulationMilliseconds;
        run.PhysicsSingleStepMaximumMilliseconds = _physics.MaximumSingleSimulationStepMilliseconds;
        run.PhysicsStaticResidencyMaximumMilliseconds = _physics.MaximumStaticResidencyMilliseconds;
        run.LoadedChunkCountAtEnd = _world.LoadedChunkCount;
        ValidateStreamingBenchmarkFinalState(run);
        CleanupStreamingBenchmarkStorage(throwOnFailure: true);
        run.EphemeralWorldDeleted = true;
        run.CompleteAndWrite();
        Console.WriteLine(run.ToConsoleSummary());
        Close();
    }

    private void ValidateStreamingBenchmarkFinalState(StreamingBenchmarkRun run)
    {
        if (!StreamingBenchmarkWorkReady())
            throw new InvalidOperationException("Benchmark completed with queued generation, mesh, collider or AS work.");
        for (int cell = 0; cell < run.SoakOriginalBlockIds.Length; cell++)
        {
            int chunkIndex = cell % 100;
            int localCell = cell / 100;
            ChunkCoordinate chunk = run.SoakChunks[chunkIndex];
            int x = chunk.MinimumX + localCell % 10;
            int z = chunk.MinimumZ + localCell / 10;
            const int y = VoxelWorld.MinimumY + 1;
            if (_world.GetBlockId(x, y, z) != run.SoakOriginalBlockIds[cell])
                throw new InvalidOperationException(
                    $"Benchmark final-state validation failed at ({x},{y},{z}).");
        }
        run.FinalStateValid = true;
    }

    private bool CleanupStreamingBenchmarkStorage(bool throwOnFailure = false)
    {
        if (_streamingBenchmarkStorageCleaned) return true;
        try
        {
            if (!string.IsNullOrWhiteSpace(_benchmarkScratchDirectory))
                DeleteConfirmedBenchmarkDirectory(
                    _benchmarkScratchDirectory,
                    Path.Combine(RuntimePaths.OutputRoot, "Benchmarks", "IO"));
            if (!string.IsNullOrWhiteSpace(_streamingBenchmarkWorldDirectory))
                DeleteConfirmedBenchmarkDirectory(_streamingBenchmarkWorldDirectory, StreamingBenchmarkWorldRoot);
            _streamingBenchmarkStorageCleaned = true;
            Console.WriteLine($"Benchmark temporary world deleted: {_streamingBenchmarkWorldName}");
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Benchmark cleanup failed: {exception.Message}");
            if (throwOnFailure) throw;
            return false;
        }
    }

    private enum StreamingBenchmarkStage
    {
        Warmup,
        WarmupBurst20,
        Burst1,
        Burst5,
        Burst10,
        Burst20,
        Burst50,
        Burst100,
        OpticalChunkPayload5120,
        BoundaryRoute,
        Place100,
        Break100,
        MaterialAndGridEdits,
        PhysicsStructure256,
        OperationSoak10000,
        RegionWrite,
        Complete,
    }

    private sealed class StreamingBenchmarkRun
    {
        private const int StableFramesRequired = 8;
        public const int OpticalOperationCount = 20 * ChunkCoordinate.Size * ChunkCoordinate.Size;
        private readonly List<double> _frameMilliseconds = new();
        private readonly Dictionary<string, StageMeasurement> _stages = new();
        private int _stableFrames;
        private int _lastAsBuildCount = -1;
        private double _stageMaximumFrameMilliseconds;
        private long _stageStarted = Stopwatch.GetTimestamp();
        private long _stageAllocatedBytesAtStart;
        private readonly int[] _stageGcCollectionsAtStart = new int[GC.MaxGeneration + 1];
        private double _maximumConsecutiveFreezeMilliseconds;
        private double _currentConsecutiveFreezeMilliseconds;
        private long _allocatedBytesAtStart;
        private readonly int[] _gcCollectionsAtStart = new int[GC.MaxGeneration + 1];
        private long _maximumManagedHeapBytes;
        private long _maximumWorkingSetBytes;
        private int _memorySampleCountdown;
        private long _lastPendingLogTimestamp;

        public RenderBackendKind Renderer { get; }
        public string Label { get; }
        public string Profile { get; }
        public string TestWorld { get; }
        public string RenderStyle { get; }
        public bool BaselineCreated { get; }
        public bool EphemeralWorldDeleted { get; set; }
        public bool FinalStateValid { get; set; }
        public int RequestedOperations { get; private set; }
        public int CompletedOperations { get; private set; }
        public double RegionReadMilliseconds { get; }
        public StreamingBenchmarkStage Stage { get; private set; } = StreamingBenchmarkStage.Warmup;
        public int BoundaryRouteIndex { get; set; }
        public int SoakOperationIndex { get; set; }
        public int PhysicsGridCountBefore { get; set; }
        public int PlaceOperationIndex { get; set; }
        public int BreakOperationIndex { get; set; }
        public int OpticalOperationIndex { get; set; }
        public ushort[]? OpticalMaterials { get; set; }
        public List<(int X, int Y, int Z)> ModificationPoints { get; } = new(100);
        public ChunkCoordinate[] SoakChunks { get; } = CreateBenchmarkBurst(centerChunkX: 72, count: 100);
        public ushort[] SoakOriginalBlockIds { get; } = new ushort[StreamingBenchmarkSoakOperations / 2];
        public int SoakBatchCount { get; private set; }
        public double SoakBatchTotalMilliseconds { get; private set; }
        public double SoakBatchMaximumMilliseconds { get; private set; }
        public int MaximumPendingJobs { get; private set; }
        public int MaximumChunksSubmittedInFrame { get; private set; }
        public int MaximumCompletedPendingCommitJobs { get; private set; }
        public int MaximumLoadedChunkCount { get; private set; }
        public double MaximumUpdateMilliseconds { get; private set; }
        public double MaximumRenderMilliseconds { get; private set; }
        public double RegionWriteMilliseconds { get; set; }
        public bool RegionWriteCompleted { get; set; }
        public double RegionSnapshotTotalMilliseconds { get; set; }
        public double RegionSnapshotMaximumStepMilliseconds { get; set; }
        public int GenerationCount { get; set; }
        public double GenerationTotalMilliseconds { get; set; }
        public double GenerationMaximumMilliseconds { get; set; }
        public int ChunkCommitCount { get; set; }
        public double ChunkCommitTotalMilliseconds { get; set; }
        public double ChunkCommitMaximumMilliseconds { get; set; }
        public int MeshBuildCount { get; set; }
        public double MeshBuildTotalMilliseconds { get; set; }
        public double MeshBuildMaximumMilliseconds { get; set; }
        public int GpuUploadCount { get; set; }
        public double GpuUploadTotalMilliseconds { get; set; }
        public double GpuUploadMaximumMilliseconds { get; set; }
        public long UploadBytes { get; set; }
        public long CpuMeshMemoryBytes { get; set; }
        public long GpuMeshMemoryBytes { get; set; }
        public int ResidentChunkMeshCount { get; set; }
        public int EvictedChunkMeshCount { get; set; }
        public int ComputeUploadCount { get; set; }
        public double ComputeUploadTotalMilliseconds { get; set; }
        public double ComputeUploadMaximumMilliseconds { get; set; }
        public long ComputeUploadBytes { get; set; }
        public int ColliderBuildCount { get; set; }
        public double ColliderBuildTotalMilliseconds { get; set; }
        public double ColliderBuildMaximumMilliseconds { get; set; }
        public double PhysicsDynamicReconcileMaximumMilliseconds { get; set; }
        public double PhysicsSimulationMaximumMilliseconds { get; set; }
        public double PhysicsSingleStepMaximumMilliseconds { get; set; }
        public double PhysicsStaticResidencyMaximumMilliseconds { get; set; }
        public int ShadowUpdateCount { get; set; }
        public double ShadowUpdateTotalMilliseconds { get; set; }
        public double ShadowUpdateMaximumMilliseconds { get; set; }
        public double ForwardShadowGpuMaximumMilliseconds { get; set; }
        public double ForwardMainGpuMaximumMilliseconds { get; set; }
        public double ForwardTransparencyGpuMaximumMilliseconds { get; set; }
        public double ForwardPostGpuMaximumMilliseconds { get; set; }
        public int DxrBuildCount { get; set; }
        public int DxrStaticBuildCount { get; set; }
        public int DxrDynamicBuildCount { get; set; }
        public int DxrTlasUpdateCount { get; set; }
        public double DxrBuildTotalMilliseconds { get; set; }
        public double DxrBuildMaximumMilliseconds { get; set; }
        public double DxrGeometryBuildMaximumMilliseconds { get; set; }
        public ulong DxrAccelerationStructureMemoryBytes { get; set; }
        public double DxrSynchronizedTraceMaximumMilliseconds { get; set; }
        public double DxrGpuTraceMaximumMilliseconds { get; set; }
        public double ReflectionTailCpuMaximumMilliseconds { get; set; }
        public ulong ReflectionTailAtlasMemoryBytes { get; set; }
        public int RealDxrReflectionLayers { get; set; }
        public int ApproximateReflectionLayers { get; set; }
        public int LoadedChunkCountAtEnd { get; set; }
        public double CurrentStageElapsedMilliseconds => BenchmarkElapsedMilliseconds(_stageStarted);

        public StreamingBenchmarkRun(
            RenderBackendKind renderer,
            string label,
            string profile,
            string testWorld,
            string renderStyle,
            bool baselineCreated,
            double regionReadMilliseconds)
        {
            Renderer = renderer;
            Label = label;
            Profile = profile;
            TestWorld = testWorld;
            RenderStyle = renderStyle;
            BaselineCreated = baselineCreated;
            RegionReadMilliseconds = regionReadMilliseconds;
            ResetMemoryBaseline();
            ResetStageMemoryBaseline();
        }

        public void ResetMeasuredFrames()
        {
            _frameMilliseconds.Clear();
            _stages.Clear();
            MaximumPendingJobs = 0;
            MaximumChunksSubmittedInFrame = 0;
            MaximumUpdateMilliseconds = 0;
            MaximumRenderMilliseconds = 0;
            MaximumCompletedPendingCommitJobs = 0;
            MaximumLoadedChunkCount = 0;
            _maximumConsecutiveFreezeMilliseconds = 0;
            _currentConsecutiveFreezeMilliseconds = 0;
            ResetMemoryBaseline();
            ResetStageMemoryBaseline();
        }

        private void ResetMemoryBaseline()
        {
            _allocatedBytesAtStart = GC.GetTotalAllocatedBytes(precise: false);
            for (int generation = 0; generation < _gcCollectionsAtStart.Length; generation++)
                _gcCollectionsAtStart[generation] = GC.CollectionCount(generation);
            _maximumManagedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
            _maximumWorkingSetBytes = Environment.WorkingSet;
            _memorySampleCountdown = 0;
        }

        private void ResetStageMemoryBaseline()
        {
            _stageAllocatedBytesAtStart = GC.GetTotalAllocatedBytes(precise: false);
            for (int generation = 0; generation < _stageGcCollectionsAtStart.Length; generation++)
                _stageGcCollectionsAtStart[generation] = GC.CollectionCount(generation);
        }

        public void RecordFrame(
            double frameMilliseconds,
            double renderMilliseconds,
            int pendingJobs,
            int completedPendingCommitJobs,
            int loadedChunkCount)
        {
            if (Stage == StreamingBenchmarkStage.Warmup) return;
            _frameMilliseconds.Add(frameMilliseconds);
            MaximumRenderMilliseconds = Math.Max(MaximumRenderMilliseconds, renderMilliseconds);
            MaximumPendingJobs = Math.Max(MaximumPendingJobs, pendingJobs);
            MaximumCompletedPendingCommitJobs = Math.Max(
                MaximumCompletedPendingCommitJobs, completedPendingCommitJobs);
            MaximumLoadedChunkCount = Math.Max(MaximumLoadedChunkCount, loadedChunkCount);
            if (_memorySampleCountdown-- <= 0)
            {
                _memorySampleCountdown = 29;
                _maximumManagedHeapBytes = Math.Max(
                    _maximumManagedHeapBytes, GC.GetTotalMemory(forceFullCollection: false));
                _maximumWorkingSetBytes = Math.Max(_maximumWorkingSetBytes, Environment.WorkingSet);
            }
            _stageMaximumFrameMilliseconds = Math.Max(_stageMaximumFrameMilliseconds, frameMilliseconds);
            if (frameMilliseconds >= 50.0)
            {
                _currentConsecutiveFreezeMilliseconds += frameMilliseconds;
                _maximumConsecutiveFreezeMilliseconds = Math.Max(
                    _maximumConsecutiveFreezeMilliseconds,
                    _currentConsecutiveFreezeMilliseconds);
            }
            else
            {
                _currentConsecutiveFreezeMilliseconds = 0;
            }
        }

        public void RecordUpdate(double updateMilliseconds, int submittedChunks, int pendingJobs)
        {
            if (Stage == StreamingBenchmarkStage.Warmup) return;
            MaximumUpdateMilliseconds = Math.Max(MaximumUpdateMilliseconds, updateMilliseconds);
            MaximumChunksSubmittedInFrame = Math.Max(MaximumChunksSubmittedInFrame, submittedChunks);
            MaximumPendingJobs = Math.Max(MaximumPendingJobs, pendingJobs);
        }

        public void RecordControllerWork(double milliseconds)
        {
            if (Stage == StreamingBenchmarkStage.Warmup) return;
            MaximumRenderMilliseconds = Math.Max(MaximumRenderMilliseconds, milliseconds);
        }

        public bool WaitForStableWork(bool ready, int asBuildCount)
        {
            if (Stage == StreamingBenchmarkStage.Warmup && _frameMilliseconds.Count == 0)
            {
                // Warm-up is driven by stability too, but it needs enough rendered frames
                // for shader compilation and the baseline meshes to settle.
                _stableFrames = ready ? _stableFrames + 1 : 0;
                return _stableFrames >= 30;
            }
            bool accelerationStable = asBuildCount == _lastAsBuildCount;
            _lastAsBuildCount = asBuildCount;
            _stableFrames = ready && accelerationStable ? _stableFrames + 1 : 0;
            return _stableFrames >= StableFramesRequired;
        }

        public void BeginStage(StreamingBenchmarkStage next)
        {
            if (Stage != StreamingBenchmarkStage.Warmup)
            {
                _stages[Stage.ToString()] = new StageMeasurement(
                    BenchmarkElapsedMilliseconds(_stageStarted),
                    _stageMaximumFrameMilliseconds,
                    Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - _stageAllocatedBytesAtStart),
                    Enumerable.Range(0, _stageGcCollectionsAtStart.Length)
                        .Select(generation => GC.CollectionCount(generation) -
                                              _stageGcCollectionsAtStart[generation])
                        .ToArray());
            }
            Stage = next;
            _stageStarted = Stopwatch.GetTimestamp();
            _stageMaximumFrameMilliseconds = 0;
            ResetStageMemoryBaseline();
            RestartStageWait();
        }

        public void RestartStageWait()
        {
            _stableFrames = 0;
            _lastAsBuildCount = -1;
        }

        public void RecordBurst(int count) => MaximumPendingJobs = Math.Max(MaximumPendingJobs, count);

        public void RecordOperations(int requested, int completed)
        {
            RequestedOperations += requested;
            CompletedOperations += completed;
        }

        public void RecordSoakBatch(int operations, double milliseconds)
        {
            if (operations <= 0) return;
            SoakBatchCount++;
            SoakBatchTotalMilliseconds += milliseconds;
            SoakBatchMaximumMilliseconds = Math.Max(SoakBatchMaximumMilliseconds, milliseconds);
        }

        public void LogPendingIfSlow(
            bool ready,
            int generation,
            int pendingCommit,
            int collider,
            int mesh,
            int dxr)
        {
            if (ready || CurrentStageElapsedMilliseconds < 2_000.0) return;
            long now = Stopwatch.GetTimestamp();
            if (_lastPendingLogTimestamp != 0 &&
                (now - _lastPendingLogTimestamp) * 1000.0 / Stopwatch.Frequency < 2_000.0) return;
            _lastPendingLogTimestamp = now;
            Console.WriteLine($"Benchmark waiting in {Stage}: generation={generation}, " +
                              $"pendingCommit={pendingCommit}, collider={collider}, mesh={mesh}, dxr={dxr}.");
        }

        public void CompleteAndWrite()
        {
            BeginStage(StreamingBenchmarkStage.Complete);
            double averageFrame = _frameMilliseconds.Count == 0 ? 0 : _frameMilliseconds.Average();
            double[] worst = _frameMilliseconds
                .OrderByDescending(value => value)
                .Take(Math.Max(1, (int)Math.Ceiling(_frameMilliseconds.Count * 0.01)))
                .ToArray();
            double onePercentLowFps = worst.Length == 0 ? 0 : 1000.0 / worst.Average();
            double[] worstPointOne = _frameMilliseconds
                .OrderByDescending(value => value)
                .Take(Math.Max(1, (int)Math.Ceiling(_frameMilliseconds.Count * 0.001)))
                .ToArray();
            double pointOnePercentLowFps = worstPointOne.Length == 0 ? 0 : 1000.0 / worstPointOne.Average();
            double[] orderedFrames = _frameMilliseconds.OrderBy(value => value).ToArray();
            int[] gcCollections = Enumerable.Range(0, _gcCollectionsAtStart.Length)
                .Select(generation => GC.CollectionCount(generation) - _gcCollectionsAtStart[generation])
                .ToArray();
            var report = new StreamingBenchmarkReport
            {
                FormatVersion = 4,
                Label = Label,
                Renderer = Renderer.ToString(),
                Profile = Profile,
                RenderStyle = RenderStyle,
                Seed = StreamingBenchmarkSeed,
                TestWorld = TestWorld,
                EphemeralWorldDeleted = EphemeralWorldDeleted,
                BaselineCreatedThisRun = BaselineCreated,
                TimestampUtc = DateTimeOffset.UtcNow,
                FrameCount = _frameMilliseconds.Count,
                AverageFps = averageFrame <= 0 ? 0 : 1000.0 / averageFrame,
                OnePercentLowFps = onePercentLowFps,
                PointOnePercentLowFps = pointOnePercentLowFps,
                P95FrameMilliseconds = Percentile(orderedFrames, 0.95),
                P99FrameMilliseconds = Percentile(orderedFrames, 0.99),
                P999FrameMilliseconds = Percentile(orderedFrames, 0.999),
                SlowestFrameMilliseconds = _frameMilliseconds.DefaultIfEmpty(0).Max(),
                MaximumConsecutiveFreezeMilliseconds = _maximumConsecutiveFreezeMilliseconds,
                MaximumMainThreadBlockMilliseconds = Math.Max(MaximumUpdateMilliseconds, MaximumRenderMilliseconds),
                MaximumUpdateMilliseconds = MaximumUpdateMilliseconds,
                MaximumRenderMilliseconds = MaximumRenderMilliseconds,
                RegionReadMilliseconds = RegionReadMilliseconds,
                RegionWriteMilliseconds = RegionWriteMilliseconds,
                RegionSnapshotTotalMilliseconds = RegionSnapshotTotalMilliseconds,
                RegionSnapshotMaximumStepMilliseconds = RegionSnapshotMaximumStepMilliseconds,
                Generation = new TimingReport(GenerationCount, GenerationTotalMilliseconds, GenerationMaximumMilliseconds),
                ChunkCommit = new TimingReport(ChunkCommitCount, ChunkCommitTotalMilliseconds, ChunkCommitMaximumMilliseconds),
                MeshBuild = new TimingReport(MeshBuildCount, MeshBuildTotalMilliseconds, MeshBuildMaximumMilliseconds),
                ColliderBuild = new TimingReport(ColliderBuildCount, ColliderBuildTotalMilliseconds, ColliderBuildMaximumMilliseconds),
                PhysicsDynamicReconcileMaximumMilliseconds = PhysicsDynamicReconcileMaximumMilliseconds,
                PhysicsSimulationMaximumMilliseconds = PhysicsSimulationMaximumMilliseconds,
                PhysicsSingleStepMaximumMilliseconds = PhysicsSingleStepMaximumMilliseconds,
                PhysicsStaticResidencyMaximumMilliseconds = PhysicsStaticResidencyMaximumMilliseconds,
                GpuUpload = new TimingReport(GpuUploadCount, GpuUploadTotalMilliseconds, GpuUploadMaximumMilliseconds),
                ComputeVoxelUpload = new TimingReport(
                    ComputeUploadCount,
                    ComputeUploadTotalMilliseconds,
                    ComputeUploadMaximumMilliseconds),
                UploadBytes = UploadBytes,
                DeferredShadowUpdate = new TimingReport(ShadowUpdateCount, ShadowUpdateTotalMilliseconds, ShadowUpdateMaximumMilliseconds),
                ForwardShadowGpuMaximumMilliseconds = ForwardShadowGpuMaximumMilliseconds,
                ForwardMainGpuMaximumMilliseconds = ForwardMainGpuMaximumMilliseconds,
                ForwardTransparencyGpuMaximumMilliseconds = ForwardTransparencyGpuMaximumMilliseconds,
                ForwardPostGpuMaximumMilliseconds = ForwardPostGpuMaximumMilliseconds,
                ForwardGpuTimestampImplemented = ForwardMainGpuMaximumMilliseconds > 0,
                DxrAccelerationStructure = new TimingReport(DxrBuildCount, DxrBuildTotalMilliseconds, DxrBuildMaximumMilliseconds),
                DxrGeometryBuildMaximumMilliseconds = DxrGeometryBuildMaximumMilliseconds,
                DxrStaticBuildCount = DxrStaticBuildCount,
                DxrDynamicBuildCount = DxrDynamicBuildCount,
                DxrTlasUpdateCount = DxrTlasUpdateCount,
                DxrAccelerationStructureMemoryBytes = DxrAccelerationStructureMemoryBytes,
                DxrSynchronizedTraceMaximumMilliseconds = DxrSynchronizedTraceMaximumMilliseconds,
                DxrGpuTraceMaximumMilliseconds = DxrGpuTraceMaximumMilliseconds,
                ReflectionTailCpuMaximumMilliseconds = ReflectionTailCpuMaximumMilliseconds,
                ReflectionTailAtlasMemoryBytes = ReflectionTailAtlasMemoryBytes,
                RealDxrReflectionLayers = RealDxrReflectionLayers,
                ApproximateReflectionLayers = ApproximateReflectionLayers,
                HardwareGpuTimestampImplemented = DxrGpuTraceMaximumMilliseconds > 0,
                MaximumChunksSubmittedInFrame = MaximumChunksSubmittedInFrame,
                MaximumQueuedJobs = MaximumPendingJobs,
                MaximumCompletedPendingCommitJobs = MaximumCompletedPendingCommitJobs,
                MaximumSaveQueueLength = 1,
                SoakOperationCount = SoakOperationIndex,
                SoakBatchCount = SoakBatchCount,
                SoakBatchTotalMilliseconds = SoakBatchTotalMilliseconds,
                SoakBatchMaximumMilliseconds = SoakBatchMaximumMilliseconds,
                RequestedOperations = RequestedOperations,
                CompletedOperations = CompletedOperations,
                DroppedOperations = Math.Max(0, RequestedOperations - CompletedOperations),
                FailedOperations = 0,
                FinalStateValid = FinalStateValid,
                GcAllocatedBytes = Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - _allocatedBytesAtStart),
                GcCollectionCount = gcCollections,
                MaximumManagedHeapBytes = _maximumManagedHeapBytes,
                MaximumWorkingSetBytes = _maximumWorkingSetBytes,
                LoadedChunkCountAtEnd = LoadedChunkCountAtEnd,
                MaximumLoadedChunkCount = MaximumLoadedChunkCount,
                AllocatedSectionCount = 0,
                SectionStorageImplemented = false,
                CpuMeshMemoryBytes = CpuMeshMemoryBytes,
                GpuMeshMemoryBytes = GpuMeshMemoryBytes,
                ResidentChunkMeshCount = ResidentChunkMeshCount,
                EvictedChunkMeshCount = EvictedChunkMeshCount,
                GpuMemoryBytes = null,
                BenchmarkPhases = new[] { "ColdLoad", "WarmUp", "Measured" },
                FrameBudget = new FrameBudgetReport(
                    FrameWorkBudget.Default.ChunkCommitBudgetMilliseconds,
                    FrameWorkBudget.Default.ColliderCommitBudgetMilliseconds,
                    FrameWorkBudget.Default.GpuUploadBudgetMilliseconds,
                    FrameWorkBudget.Default.DxrUpdateBudgetMilliseconds,
                    FrameWorkBudget.Default.MaximumChunkCommitsPerFrame,
                    FrameWorkBudget.Default.MaximumColliderCommitsPerFrame,
                    FrameWorkBudget.Default.MaximumUploadsPerFrame,
                    FrameWorkBudget.Default.MaximumUploadBytesPerFrame),
                Stages = new Dictionary<string, StageMeasurement>(_stages),
                AcceptanceNoOneSecondFreeze = _frameMilliseconds.DefaultIfEmpty(0).Max() < 1000.0,
                AcceptanceMinimum100Fps = averageFrame > 0 && 1000.0 / averageFrame >= 100.0,
                Target150Fps = averageFrame > 0 && 1000.0 / averageFrame >= 150.0,
            };

            string reportRoot = Path.Combine(RuntimePaths.OutputRoot, "Benchmarks", "Reports");
            Directory.CreateDirectory(reportRoot);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            string stem = $"world-streaming-{Label}-{Renderer}-{stamp}";
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(reportRoot, stem + ".json"), json);
            File.WriteAllText(Path.Combine(reportRoot, $"latest-{Label}-{Renderer}.json"), json);
            File.WriteAllText(Path.Combine(reportRoot, stem + ".md"), report.ToMarkdown());
        }

        private static double Percentile(IReadOnlyList<double> ordered, double percentile)
        {
            if (ordered.Count == 0) return 0;
            double position = Math.Clamp(percentile, 0, 1) * (ordered.Count - 1);
            int lower = (int)Math.Floor(position);
            int upper = (int)Math.Ceiling(position);
            if (lower == upper) return ordered[lower];
            double fraction = position - lower;
            return ordered[lower] + (ordered[upper] - ordered[lower]) * fraction;
        }

        public string ToConsoleSummary()
        {
            double average = _frameMilliseconds.Count == 0 ? 0 : _frameMilliseconds.Average();
            return $"Streaming benchmark complete [{Label}/{Renderer}]: avg " +
                   $"{(average <= 0 ? 0 : 1000.0 / average):0.0} FPS, slowest " +
                   $"{_frameMilliseconds.DefaultIfEmpty(0).Max():0.00} ms, main block " +
                   $"{Math.Max(MaximumUpdateMilliseconds, MaximumRenderMilliseconds):0.00} ms, " +
                   $"generation max {GenerationMaximumMilliseconds:0.00} ms, " +
                   $"mesh max {MeshBuildMaximumMilliseconds:0.00} ms, collider max " +
                   $"{ColliderBuildMaximumMilliseconds:0.00} ms, DXR AS max {DxrBuildMaximumMilliseconds:0.00} ms.";
        }
    }

    private sealed class StreamingBenchmarkReport
    {
        public int FormatVersion { get; init; }
        public string Label { get; init; } = string.Empty;
        public string Renderer { get; init; } = string.Empty;
        public string Profile { get; init; } = string.Empty;
        public string RenderStyle { get; init; } = string.Empty;
        public int Seed { get; init; }
        public string TestWorld { get; init; } = string.Empty;
        public bool EphemeralWorldDeleted { get; init; }
        public bool BaselineCreatedThisRun { get; init; }
        public DateTimeOffset TimestampUtc { get; init; }
        public int FrameCount { get; init; }
        public double AverageFps { get; init; }
        public double OnePercentLowFps { get; init; }
        public double PointOnePercentLowFps { get; init; }
        public double P95FrameMilliseconds { get; init; }
        public double P99FrameMilliseconds { get; init; }
        public double P999FrameMilliseconds { get; init; }
        public double SlowestFrameMilliseconds { get; init; }
        public double MaximumConsecutiveFreezeMilliseconds { get; init; }
        public double MaximumMainThreadBlockMilliseconds { get; init; }
        public double MaximumUpdateMilliseconds { get; init; }
        public double MaximumRenderMilliseconds { get; init; }
        public double RegionReadMilliseconds { get; init; }
        public double RegionWriteMilliseconds { get; init; }
        public double RegionSnapshotTotalMilliseconds { get; init; }
        public double RegionSnapshotMaximumStepMilliseconds { get; init; }
        public TimingReport Generation { get; init; } = new(0, 0, 0);
        public TimingReport ChunkCommit { get; init; } = new(0, 0, 0);
        public TimingReport MeshBuild { get; init; } = new(0, 0, 0);
        public TimingReport ColliderBuild { get; init; } = new(0, 0, 0);
        public double PhysicsDynamicReconcileMaximumMilliseconds { get; init; }
        public double PhysicsSimulationMaximumMilliseconds { get; init; }
        public double PhysicsSingleStepMaximumMilliseconds { get; init; }
        public double PhysicsStaticResidencyMaximumMilliseconds { get; init; }
        public TimingReport GpuUpload { get; init; } = new(0, 0, 0);
        public TimingReport ComputeVoxelUpload { get; init; } = new(0, 0, 0);
        public long UploadBytes { get; init; }
        public TimingReport DeferredShadowUpdate { get; init; } = new(0, 0, 0);
        public double ForwardShadowGpuMaximumMilliseconds { get; init; }
        public double ForwardMainGpuMaximumMilliseconds { get; init; }
        public double ForwardTransparencyGpuMaximumMilliseconds { get; init; }
        public double ForwardPostGpuMaximumMilliseconds { get; init; }
        public bool ForwardGpuTimestampImplemented { get; init; }
        public TimingReport DxrAccelerationStructure { get; init; } = new(0, 0, 0);
        public double DxrGeometryBuildMaximumMilliseconds { get; init; }
        public int DxrStaticBuildCount { get; init; }
        public int DxrDynamicBuildCount { get; init; }
        public int DxrTlasUpdateCount { get; init; }
        public ulong DxrAccelerationStructureMemoryBytes { get; init; }
        public double DxrSynchronizedTraceMaximumMilliseconds { get; init; }
        public double DxrGpuTraceMaximumMilliseconds { get; init; }
        public double ReflectionTailCpuMaximumMilliseconds { get; init; }
        public ulong ReflectionTailAtlasMemoryBytes { get; init; }
        public int RealDxrReflectionLayers { get; init; }
        public int ApproximateReflectionLayers { get; init; }
        public bool HardwareGpuTimestampImplemented { get; init; }
        public int MaximumChunksSubmittedInFrame { get; init; }
        public int MaximumQueuedJobs { get; init; }
        public int MaximumCompletedPendingCommitJobs { get; init; }
        public int MaximumSaveQueueLength { get; init; }
        public int SoakOperationCount { get; init; }
        public int SoakBatchCount { get; init; }
        public double SoakBatchTotalMilliseconds { get; init; }
        public double SoakBatchMaximumMilliseconds { get; init; }
        public int RequestedOperations { get; init; }
        public int CompletedOperations { get; init; }
        public int DroppedOperations { get; init; }
        public int FailedOperations { get; init; }
        public bool FinalStateValid { get; init; }
        public long GcAllocatedBytes { get; init; }
        public int[] GcCollectionCount { get; init; } = Array.Empty<int>();
        public long MaximumManagedHeapBytes { get; init; }
        public long MaximumWorkingSetBytes { get; init; }
        public int LoadedChunkCountAtEnd { get; init; }
        public int MaximumLoadedChunkCount { get; init; }
        public int AllocatedSectionCount { get; init; }
        public bool SectionStorageImplemented { get; init; }
        public long CpuMeshMemoryBytes { get; init; }
        public long GpuMeshMemoryBytes { get; init; }
        public int ResidentChunkMeshCount { get; init; }
        public int EvictedChunkMeshCount { get; init; }
        public long? GpuMemoryBytes { get; init; }
        public string[] BenchmarkPhases { get; init; } = Array.Empty<string>();
        public FrameBudgetReport FrameBudget { get; init; } = new(0, 0, 0, 0, 0, 0, 0, 0);
        public IReadOnlyDictionary<string, StageMeasurement> Stages { get; init; } =
            new Dictionary<string, StageMeasurement>();
        public bool AcceptanceNoOneSecondFreeze { get; init; }
        public bool AcceptanceMinimum100Fps { get; init; }
        public bool Target150Fps { get; init; }

        public string ToMarkdown() =>
            $"# RTX Sandbox World Streaming Benchmark\n\n" +
            $"- Label: `{Label}`\n- Renderer: `{Renderer}`\n- Profile/style: `{Profile}/{RenderStyle}`\n" +
            $"- Ephemeral world: `{TestWorld}` (seed `{Seed}`, deleted: `{EphemeralWorldDeleted}`)\n" +
            $"- Average FPS: `{AverageFps:0.00}`\n- 1% Low: `{OnePercentLowFps:0.00}` FPS\n" +
            $"- 0.1% Low: `{PointOnePercentLowFps:0.00}` FPS\n" +
            $"- Frame time p95/p99/p99.9: `{P95FrameMilliseconds:0.00}/{P99FrameMilliseconds:0.00}/{P999FrameMilliseconds:0.00} ms`\n" +
            $"- Slowest frame: `{SlowestFrameMilliseconds:0.00} ms`\n" +
            $"- Longest continuous freeze: `{MaximumConsecutiveFreezeMilliseconds:0.00} ms`\n" +
            $"- Longest main-thread block: `{MaximumMainThreadBlockMilliseconds:0.00} ms`\n" +
            $"- Region read/write: `{RegionReadMilliseconds:0.00}/{RegionWriteMilliseconds:0.00} ms`\n" +
            $"- Region snapshot total/max step: `{RegionSnapshotTotalMilliseconds:0.00}/{RegionSnapshotMaximumStepMilliseconds:0.00} ms`\n" +
            $"- Chunk generation count/total/max: `{Generation.Count}/{Generation.TotalMilliseconds:0.00}/{Generation.MaximumMilliseconds:0.00} ms`\n" +
            $"- Chunk commit count/total/max: `{ChunkCommit.Count}/{ChunkCommit.TotalMilliseconds:0.00}/{ChunkCommit.MaximumMilliseconds:0.00} ms`\n" +
            $"- Mesh build count/total/max: `{MeshBuild.Count}/{MeshBuild.TotalMilliseconds:0.00}/{MeshBuild.MaximumMilliseconds:0.00} ms`\n" +
             $"- Collider build count/total/max: `{ColliderBuild.Count}/{ColliderBuild.TotalMilliseconds:0.00}/{ColliderBuild.MaximumMilliseconds:0.00} ms`\n" +
             $"- Physics dynamic reconcile/simulation/static residency max: `{PhysicsDynamicReconcileMaximumMilliseconds:0.00}/" +
             $"{PhysicsSimulationMaximumMilliseconds:0.00}/{PhysicsStaticResidencyMaximumMilliseconds:0.00} ms`\n" +
             $"- Physics single fixed-step max: `{PhysicsSingleStepMaximumMilliseconds:0.00} ms`\n" +
            $"- GPU upload count/total/max: `{GpuUpload.Count}/{GpuUpload.TotalMilliseconds:0.00}/{GpuUpload.MaximumMilliseconds:0.00} ms`\n" +
            $"- Compute voxel upload count/total/max: `{ComputeVoxelUpload.Count}/{ComputeVoxelUpload.TotalMilliseconds:0.00}/{ComputeVoxelUpload.MaximumMilliseconds:0.00} ms`\n" +
            $"- Upload bytes: `{UploadBytes:N0}`\n" +
            $"- Deferred shadow update count/total/max: `{DeferredShadowUpdate.Count}/{DeferredShadowUpdate.TotalMilliseconds:0.00}/{DeferredShadowUpdate.MaximumMilliseconds:0.00} ms`\n" +
            $"- Forward GPU max shadow/main/transparent/post: `{ForwardShadowGpuMaximumMilliseconds:0.00}/" +
            $"{ForwardMainGpuMaximumMilliseconds:0.00}/{ForwardTransparencyGpuMaximumMilliseconds:0.00}/" +
            $"{ForwardPostGpuMaximumMilliseconds:0.00} ms` (timestamp implemented: `{ForwardGpuTimestampImplemented}`)\n" +
            $"- DXR AS count/total/max: `{DxrAccelerationStructure.Count}/{DxrAccelerationStructure.TotalMilliseconds:0.00}/{DxrAccelerationStructure.MaximumMilliseconds:0.00} ms`\n" +
            $"- DXR CPU geometry build max: `{DxrGeometryBuildMaximumMilliseconds:0.00} ms`\n" +
            $"- DXR static/dynamic/TLAS updates: `{DxrStaticBuildCount}/{DxrDynamicBuildCount}/{DxrTlasUpdateCount}`\n" +
            $"- DXR AS memory: `{DxrAccelerationStructureMemoryBytes:N0} bytes`\n" +
            $"- DXR synchronized trace max: `{DxrSynchronizedTraceMaximumMilliseconds:0.00} ms` " +
            $"(GPU timestamp implemented: `{HardwareGpuTimestampImplemented}`)\n" +
            $"- DXR DispatchRays GPU max: `{DxrGpuTraceMaximumMilliseconds:0.00} ms` " +
            $"(Reflection Tail is integrated in this dispatch)\n" +
            $"- Reflection Tail CPU max / atlas memory: `{ReflectionTailCpuMaximumMilliseconds:0.00} ms / " +
            $"{ReflectionTailAtlasMemoryBytes:N0} bytes`\n" +
            $"- Real DXR / approximate reflection layers: `{RealDxrReflectionLayers}/{ApproximateReflectionLayers}`\n" +
            $"- Max Chunk submits/frame: `{MaximumChunksSubmittedInFrame}`\n" +
            $"- Max queued jobs: `{MaximumQueuedJobs}`\n" +
            $"- Frame budget commit/collider/upload/DXR: `{FrameBudget.ChunkCommitMilliseconds:0.0}/" +
            $"{FrameBudget.ColliderCommitMilliseconds:0.0}/{FrameBudget.GpuUploadMilliseconds:0.0}/" +
            $"{FrameBudget.DxrUpdateMilliseconds:0.0} ms`\n" +
            $"- Max completed pending commit: `{MaximumCompletedPendingCommitJobs}`\n" +
            $"- 10,000-operation soak operations/batches/total/max batch: `{SoakOperationCount}/" +
            $"{SoakBatchCount}/{SoakBatchTotalMilliseconds:0.00}/{SoakBatchMaximumMilliseconds:0.00} ms`\n" +
            $"- Operations requested/completed/dropped/failed: `{RequestedOperations}/{CompletedOperations}/{DroppedOperations}/{FailedOperations}`\n" +
            $"- Final-state validation: `{FinalStateValid}`\n" +
            $"- GC allocated/collections: `{GcAllocatedBytes:N0} bytes / {string.Join(",", GcCollectionCount)}`\n" +
            $"- Managed heap/working set max: `{MaximumManagedHeapBytes:N0}/{MaximumWorkingSetBytes:N0} bytes`\n" +
            $"- Loaded Chunks end/max: `{LoadedChunkCountAtEnd}/{MaximumLoadedChunkCount}`\n" +
            $"- CPU/GPU mesh memory: `{CpuMeshMemoryBytes:N0}/{GpuMeshMemoryBytes:N0} bytes`\n" +
            $"- Resident/evicted Chunk meshes: `{ResidentChunkMeshCount}/{EvictedChunkMeshCount}`\n" +
            $"- Section storage: `{(SectionStorageImplemented ? AllocatedSectionCount.ToString() : "not implemented")}`\n" +
            $"- GPU memory: `{(GpuMemoryBytes.HasValue ? GpuMemoryBytes.Value.ToString("N0") : "unavailable (no reliable API)")}`\n" +
            $"- Acceptance (<1 second freeze): `{AcceptanceNoOneSecondFreeze}`\n" +
            $"- Acceptance (average >= 100 FPS): `{AcceptanceMinimum100Fps}`\n" +
            $"- Target (average >= 150 FPS): `{Target150Fps}`\n";
    }

    private sealed record TimingReport(int Count, double TotalMilliseconds, double MaximumMilliseconds);
    private sealed record StageMeasurement(
        double WallMilliseconds,
        double MaximumFrameMilliseconds,
        long AllocatedBytes,
        int[] GcCollectionCount);
    private sealed record FrameBudgetReport(
        double ChunkCommitMilliseconds,
        double ColliderCommitMilliseconds,
        double GpuUploadMilliseconds,
        double DxrUpdateMilliseconds,
        int MaximumChunkCommits,
        int MaximumColliderCommits,
        int MaximumUploads,
        long MaximumUploadBytes);

    private static double BenchmarkElapsedMilliseconds(long started) =>
        (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
}
