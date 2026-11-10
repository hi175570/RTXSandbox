using System.Numerics;
using System.Reflection;
using BepInEx;
using BepInEx.NET.Common;
using HarmonyLib;
using RTXSandbox;
using RTXSandbox.Engine;
using RTXSandbox.Gameplay;
using RTXSandbox.World;

namespace RTXSandbox.SoftBodyPhysicsMod;

[BepInPlugin(PluginId, PluginName, PluginVersion)]
public sealed class PlasticDeformationPlugin : BasePlugin
{
    public const string PluginId = "rtxsandbox.soft-body-physics";
    public const string PluginName = "RTX Sandbox 材質形變";
    public const string PluginVersion = "2.1.0";
    private static PlasticDeformationPlugin? _instance;
    private SandboxGame? _game;

    public override void Load()
    {
        _instance = this;
        MethodInfo loaded = AccessTools.Method(typeof(SandboxGame), "OnLoad")
            ?? throw new MissingMethodException(nameof(SandboxGame), "OnLoad");
        MethodInfo impact = AccessTools.Method(typeof(EnergyProjectileSystem), "ApplyImpact")
            ?? throw new MissingMethodException(nameof(EnergyProjectileSystem), "ApplyImpact");
        HarmonyInstance.Patch(loaded,
            postfix: new HarmonyMethod(typeof(PlasticDeformationPlugin), nameof(GameLoaded)));
        HarmonyInstance.Patch(impact,
            postfix: new HarmonyMethod(typeof(PlasticDeformationPlugin), nameof(EnergyImpactApplied)));
        Log.LogInfo("材質形變 V2 已載入：只在能量命中時運算，不持續模擬全世界。原版軟體物理不依賴此模組。");
    }

    private static void GameLoaded(SandboxGame __instance)
    {
        _instance!._game = __instance;
        if (Environment.GetEnvironmentVariable("RTX_SANDBOX_GPU_SMOKE_MOD") == "1")
            _instance.RunSmokeProbe();
    }

    private static void EnergyImpactApplied(EnergyImpact impact) => _instance?.Deform(impact);

    private void Deform(EnergyImpact impact)
    {
        if (_game is null || impact.MaterialId == 0) return;
        VoxelWorld world = _game.World;
        BlockRegistry blocks = _game.Blocks;
        BlockDefinition struck = blocks.Get(impact.MaterialId);
        if (struck.IsFluid || struck.IsUnbreakable || IsGlass(struck)) return;

        MaterialResponse response = ResponseFor(struck);
        float pressure = impact.Force / MathF.Max(0.08f, MathF.PI * impact.Radius * impact.Radius);
        if (pressure < response.YieldPressure) return;
        float normalized = Math.Clamp((pressure - response.YieldPressure) /
                                      MathF.Max(response.YieldPressure * 2f, 1f), 0.08f, 1f);
        if (impact.GridId != 0)
        {
            if (impact.GridId < ulong.MaxValue / 2 &&
                world.PhysicsGrids.Get(impact.GridId) is PhysicsGrid deformation &&
                deformation.IsStaticDeformation && response.Shape == DentShape.FacetedMetal)
                ApplyAdditionalMetalDent(deformation, impact, response, normalized);
            return;
        }
        if (response.Shape == DentShape.FacetedMetal)
        {
            int metalChanged = ApplyContinuousMetalDent(world, impact, pressure, normalized);
            if (metalChanged > 0)
                Log.LogInfo($"{struck.DisplayName} 命中：{metalChanged} 格共用金屬補丁，" +
                            $"力道 {impact.Force:0.#}，半徑 {impact.Radius:0.00}，壓力 {pressure:0.00}。");
            return;
        }
        int radius = Math.Clamp((int)MathF.Ceiling(impact.Radius), 1, 4);
        int changed = 0;
        for (int z = impact.Z - radius; z <= impact.Z + radius; z++)
        for (int y = impact.Y - radius; y <= impact.Y + radius; y++)
        for (int x = impact.X - radius; x <= impact.X + radius; x++)
        {
            ushort materialId = world.GetBlockId(x, y, z);
            if (materialId == 0) continue;
            BlockDefinition material = blocks.Get(materialId);
            if (material.IsFluid || material.IsUnbreakable || IsGlass(material)) continue;
            Vector3 cellCenter = new(x + 0.5f, y + 0.5f, z + 0.5f);
            float distance = DistanceToImpactPlane(cellCenter - impact.Position, impact.Normal);
            if (distance > impact.Radius + 0.87f) continue;
            MaterialResponse localResponse = ResponseFor(material);
            if (pressure < localResponse.YieldPressure) continue;
            float falloff = 1f - Math.Clamp(distance / MathF.Max(impact.Radius + 0.87f, 0.1f), 0f, 1f);
            if (ApplyDent(world, x, y, z, materialId, impact, localResponse,
                    normalized * falloff)) changed++;
        }
        if (changed > 0)
            Log.LogInfo($"{struck.DisplayName} 命中：{changed} 格，{response.Shape}，" +
                        $"力道 {impact.Force:0.#}，半徑 {impact.Radius:0.00}，壓力 {pressure:0.00}。");
    }

    private void RunSmokeProbe()
    {
        const int x = 55;
        const int y = 40;
        const int z = 55;
        if (_game is null || !_game.Blocks.TryGet("rtxsandbox:iron_block", out BlockDefinition? iron))
            throw new InvalidOperationException("Material deformation smoke cannot find the core iron block.");
        VoxelWorld world = _game.World;
        ushort original = world.GetBlockId(x, y, z);
        ushort neighborOriginal = world.GetBlockId(x, y + 1, z);
        MicroVoxelContainer? originalMicro = world.TryGetMicroVoxels(x, y, z, out MicroVoxelContainer? existing)
            ? new MicroVoxelContainer(existing!.Snapshot())
            : null;
        MicroVoxelContainer? neighborMicro = world.TryGetMicroVoxels(x, y + 1, z, out MicroVoxelContainer? neighborExisting)
            ? new MicroVoxelContainer(neighborExisting!.Snapshot())
            : null;
        HashSet<ulong> originalGridIds = world.PhysicsGrids.Grids.Select(grid => grid.Id).ToHashSet();
        try
        {
            world.SetBlockId(x, y, z, iron!.Id);
            world.SetBlockId(x, y + 1, z, iron.Id);
            Deform(new EnergyImpact(
                new Vector3(x + 1f, y + 0.5f, z + 0.5f), Vector3.UnitX, Vector3.UnitX,
                180f, 0.35f, iron.Id, x, y, z, 0));
            PhysicsGrid? dented = world.PhysicsGrids.Grids.FirstOrDefault(grid =>
                !originalGridIds.Contains(grid.Id) && grid.IsStaticDeformation);
            if (world.GetBlockId(x, y, z) != 0 || world.GetBlockId(x, y + 1, z) != 0 || dented is null ||
                dented.SmoothSpheres.Count != 2 || dented.Elements.Count != 2 ||
                dented.SmoothSpheres.Any(cell => !cell.HasExplicitCorners))
                throw new InvalidOperationException("Material deformation smoke did not produce one cross-block metal patch.");

            int firstDentVersion = dented.TopologyVersion;
            Vector3[] firstDentCorners = dented.SmoothSpheres
                .SelectMany(cell => cell.ExplicitCorners!)
                .ToArray();
            Vector3 repeatedHitPosition = dented.LocalToWorld(new Vector3(1f, 0.5f, 0.5f));
            Deform(new EnergyImpact(
                repeatedHitPosition, Vector3.UnitX, Vector3.UnitX,
                180f, 0.35f, iron.Id, x, y, z, dented.Id));
            Vector3[] secondDentCorners = dented.SmoothSpheres
                .SelectMany(cell => cell.ExplicitCorners!)
                .ToArray();
            if (dented.TopologyVersion <= firstDentVersion ||
                !firstDentCorners.Zip(secondDentCorners,
                    (before, after) => Vector3.DistanceSquared(before, after))
                    .Any(delta => delta > 0.000001f))
                throw new InvalidOperationException("Material deformation smoke could not dent metal a second time.");
            if (!dented.TryRemoveCell(0, 0, 0) || dented.Elements.Count != 1 ||
                dented.SmoothSpheres.Count != 1)
                throw new InvalidOperationException("A dented source block did not remain independently editable.");

            Log.LogInfo("MATERIAL_DEFORMATION_SMOKE PASS: cross-block iron patch stayed continuous, repeatable and source-block editable without Micro Voxel stairs or a duplicate rigid body.");
        }
        finally
        {
            foreach (PhysicsGrid added in world.PhysicsGrids.Grids
                         .Where(grid => !originalGridIds.Contains(grid.Id)).ToArray())
                world.PhysicsGrids.Remove(added);
            world.SetBlockId(x, y, z, original);
            world.SetMicroVoxels(x, y, z, originalMicro);
            world.SetBlockId(x, y + 1, z, neighborOriginal);
            world.SetMicroVoxels(x, y + 1, z, neighborMicro);
        }
    }

    private bool ApplyDent(
        VoxelWorld world,
        int worldX,
        int worldY,
        int worldZ,
        ushort materialId,
        EnergyImpact impact,
        MaterialResponse response,
        float strength)
    {
        if (strength <= 0.015f) return false;
        const int resolution = 8;
        MicroVoxelContainer micro = world.TryGetMicroVoxels(worldX, worldY, worldZ, out MicroVoxelContainer? existing)
            ? new MicroVoxelContainer(existing!.Snapshot())
            : Filled(materialId);
        Vector3 localImpact = impact.Position - new Vector3(worldX, worldY, worldZ);
        int axis = DominantAxis(impact.Normal);
        float sign = Axis(impact.Normal, axis) >= 0f ? 1f : -1f;
        (float centerU, float centerV) = Project(localImpact, axis);
        int seed = HashCode.Combine(world.Seed, worldX, worldY, worldZ,
            (int)(impact.Force * 100f), response.Shape);
        var random = new Random(seed);
        bool changed = false;

        for (int z = 0; z < resolution; z++)
        for (int y = 0; y < resolution; y++)
        for (int x = 0; x < resolution; x++)
        {
            Vector3 sample = new((x + 0.5f) / resolution, (y + 0.5f) / resolution, (z + 0.5f) / resolution);
            (float u, float v) = Project(sample, axis);
            float du = u - centerU;
            float dv = v - centerV;
            float lateral = response.Shape switch
            {
                DentShape.FacetedMetal => MathF.Max(MathF.Abs(du), MathF.Abs(dv)) +
                                          0.11f * MathF.Abs(du + dv),
                DentShape.Chipped => MathF.Sqrt(du * du + dv * dv) *
                                     (0.78f + (float)random.NextDouble() * 0.44f),
                _ => MathF.Sqrt(du * du + dv * dv),
            };
            float radius = Math.Clamp(impact.Radius * 0.55f + 0.18f, 0.16f, 0.72f);
            if (lateral > radius) continue;
            float radial = 1f - lateral / radius;
            float profile = response.Shape switch
            {
                DentShape.SmoothBowl => radial * radial * (3f - 2f * radial),
                DentShape.FacetedMetal => MathF.Floor(radial * 4f) / 4f,
                DentShape.Chipped => radial * (0.72f + (float)random.NextDouble() * 0.55f),
                _ => radial,
            };
            float depth = Math.Clamp(response.MaximumDepth * strength * profile, 0f, 0.78f);
            float coordinate = Axis(sample, axis);
            float inward = sign > 0f ? 1f - coordinate : coordinate;
            bool crease = response.Shape == DentShape.FacetedMetal &&
                          (MathF.Abs(du - dv) < 0.035f || MathF.Abs(du + dv) < 0.035f);
            if (inward > depth + (crease ? 0.09f : 0f)) continue;
            micro.PaintCell(resolution, x, y, z, 0);
            changed = true;
        }
        if (!changed) return false;
        world.SetMicroVoxels(worldX, worldY, worldZ, micro.IsEmpty ? null : micro);
        return true;

        static MicroVoxelContainer Filled(ushort id)
        {
            var result = new MicroVoxelContainer(resolution);
            result.Fill(id);
            return result;
        }
    }

    private int ApplyContinuousMetalDent(
        VoxelWorld world,
        EnergyImpact impact,
        float pressure,
        float strength)
    {
        int normalAxis = DominantAxis(impact.Normal);
        float sign = Axis(impact.Normal, normalAxis) >= 0f ? 1f : -1f;
        // Radius describes the contact footprint around the hit point. The authored
        // surface has one vertex per source-block corner, so include the half-cell
        // corner kernel; otherwise a centred sub-block hit is 0.707 from every vertex
        // and incorrectly produces no deformation at all.
        float radius = Math.Clamp(impact.Radius + 0.72f, 0.80f, 4.72f);
        int searchRadius = Math.Clamp((int)MathF.Ceiling(radius + 0.75f), 1, 5);
        int fixedLayer = normalAxis == 0 ? impact.X : normalAxis == 1 ? impact.Y : impact.Z;
        float phase = (HashCode.Combine(world.Seed, impact.X, impact.Y, impact.Z) & 1023) /
                      1023f * MathF.Tau;
        var candidates = new List<MetalCell>();

        for (int dv = -searchRadius; dv <= searchRadius; dv++)
        for (int du = -searchRadius; du <= searchRadius; du++)
        {
            (int x, int y, int z) = normalAxis switch
            {
                0 => (fixedLayer, impact.Y + dv, impact.Z + du),
                1 => (impact.X + du, fixedLayer, impact.Z + dv),
                _ => (impact.X + du, impact.Y + dv, fixedLayer),
            };
            ushort materialId = world.GetBlockId(x, y, z);
            if (materialId == 0) continue;
            BlockDefinition material = _game!.Blocks.Get(materialId);
            MaterialResponse response = ResponseFor(material);
            if (response.Shape != DentShape.FacetedMetal || material.IsUnbreakable ||
                pressure < response.YieldPressure) continue;
            if (world.TryGetMicroVoxels(x, y, z, out MicroVoxelContainer? existing) &&
                existing is not null && !existing.TryGetFullBlock(out _)) continue;

            float localStrength = Math.Clamp(
                (pressure - response.YieldPressure) / MathF.Max(response.YieldPressure * 2f, 1f),
                0.08f,
                strength);
            var cell = new MetalCell(x, y, z, materialId, response, localStrength);
            if (MaximumCellDent(cell) > 0.002f) candidates.Add(cell);
        }
        if (candidates.Count == 0) return 0;

        int minX = candidates.Min(cell => cell.X);
        int minY = candidates.Min(cell => cell.Y);
        int minZ = candidates.Min(cell => cell.Z);
        Vector3 origin = new(minX, minY, minZ);
        var consumed = new List<MetalCell>(candidates.Count);
        foreach (MetalCell cell in candidates)
        {
            if (_game!.Physics.TryConsumeForStaticDeformation(
                    cell.X, cell.Y, cell.Z, cell.MaterialId))
            {
                consumed.Add(cell);
                continue;
            }

            // This path is only a race/invalid-state guard. Restore anything already
            // consumed rather than leaving half of one impact converted to another body.
            foreach (MetalCell previous in consumed)
                world.SetBlockId(previous.X, previous.Y, previous.Z, previous.MaterialId);
            return 0;
        }

        var elements = new List<PhysicsGridElement>(candidates.Count);
        var skin = new List<PhysicsGridSmoothSphere>(candidates.Count);
        foreach (MetalCell cell in candidates)
        {
            int cellX = cell.X - minX;
            int cellY = cell.Y - minY;
            int cellZ = cell.Z - minZ;
            Vector3 minimum = new(cellX, cellY, cellZ);
            elements.Add(new PhysicsGridElement(
                cellX, cellY, cellZ, minimum, minimum + Vector3.One, cell.MaterialId));

            var corners = new Vector3[8];
            for (int zBit = 0; zBit <= 1; zBit++)
            for (int yBit = 0; yBit <= 1; yBit++)
            for (int xBit = 0; xBit <= 1; xBit++)
            {
                Vector3 local = minimum + new Vector3(xBit, yBit, zBit);
                Vector3 worldCorner = origin + local;
                int normalBit = normalAxis == 0 ? xBit : normalAxis == 1 ? yBit : zBit;
                bool struckFace = sign > 0f ? normalBit == 1 : normalBit == 0;
                if (struckFace)
                    local -= Vector3.Normalize(impact.Normal) * DentDepth(worldCorner, cell);
                corners[xBit + yBit * 2 + zBit * 4] = local;
            }
            Vector3 center = corners.Aggregate(Vector3.Zero, (sum, point) => sum + point) / 8f;
            skin.Add(new PhysicsGridSmoothSphere(
                center, 0.5f, cell.MaterialId, cellX, cellY, cellZ)
            {
                ExplicitCorners = corners,
            });
        }

        PhysicsGrid grid = world.PhysicsGrids.Create(
            elements, origin, id => _game!.Blocks.Get(id).Density);
        grid.SetSmoothSpheres(skin);
        grid.FractureOnImpact = false;
        grid.FreezeAsStaticDeformation();
        world.PhysicsGrids.NotifyChanged();
        return candidates.Count;

        float MaximumCellDent(MetalCell cell)
        {
            float maximum = 0f;
            for (int v = 0; v <= 1; v++)
            for (int u = 0; u <= 1; u++)
            {
                Vector3 corner = normalAxis switch
                {
                    0 => new Vector3(cell.X + (sign > 0f ? 1f : 0f), cell.Y + v, cell.Z + u),
                    1 => new Vector3(cell.X + u, cell.Y + (sign > 0f ? 1f : 0f), cell.Z + v),
                    _ => new Vector3(cell.X + u, cell.Y + v, cell.Z + (sign > 0f ? 1f : 0f)),
                };
                maximum = MathF.Max(maximum, DentDepth(corner, cell));
            }
            return maximum;
        }

        float DentDepth(Vector3 worldCorner, MetalCell cell)
        {
            Vector3 tangent = worldCorner - impact.Position;
            Vector3 normal = Vector3.Normalize(impact.Normal);
            tangent -= normal * Vector3.Dot(tangent, normal);
            (float du, float dv) = Project(tangent, normalAxis);
            float facetedDistance = MathF.Max(MathF.Abs(du) * 1.06f, MathF.Abs(dv) * 0.94f) +
                                     0.10f * MathF.Abs(du + dv);
            if (facetedDistance >= radius) return 0f;
            float radial = 1f - facetedDistance / radius;
            float maximumDepth = Math.Clamp(cell.Response.MaximumDepth * cell.Strength * 0.58f,
                0.025f, 0.24f);
            // A flat yielded centre and narrow angled crease band produce folded sheet
            // metal. With one authored vertex per source-block corner, adjacent blocks
            // share exactly the same surface and cannot turn into a smooth rubber bowl.
            float plateau = radial >= 0.58f
                ? 1f
                : radial <= 0.12f ? 0f : (radial - 0.12f) / 0.46f;
            float angle = MathF.Atan2(dv, du);
            float workHardening = 0.92f + 0.08f * MathF.Abs(MathF.Cos(angle * 3f + phase));
            return maximumDepth * plateau * workHardening;
        }
    }

    private bool ApplyAdditionalMetalDent(
        PhysicsGrid grid,
        EnergyImpact impact,
        MaterialResponse response,
        float strength)
    {
        if (grid.SmoothSpheres.Count == 0 ||
            grid.SmoothSpheres.Any(cell => !cell.HasExplicitCorners)) return false;

        Vector3 localImpact = grid.WorldToLocal(impact.Position);
        Vector3 localNormal = Vector3.Normalize(Vector3.Transform(
            impact.Normal, Quaternion.Inverse(grid.Orientation)));
        int normalAxis = DominantAxis(localNormal);
        float sign = Axis(localNormal, normalAxis) >= 0f ? 1f : -1f;
        float radius = Math.Clamp(impact.Radius + 0.72f, 0.80f, 4.72f);
        float increment = Math.Clamp(response.MaximumDepth * strength * 0.32f, 0.012f, 0.11f);
        int seed = HashCode.Combine(grid.Id, grid.TopologyVersion,
            (int)MathF.Round(impact.Position.X * 64f), (int)MathF.Round(impact.Position.Y * 64f));
        float phase = (seed & 1023) / 1023f * MathF.Tau;
        var replacement = new List<PhysicsGridSmoothSphere>(grid.SmoothSpheres.Count);
        bool changed = false;
        foreach (PhysicsGridSmoothSphere cell in grid.SmoothSpheres)
        {
            Vector3[] corners = cell.ExplicitCorners!.ToArray();
            for (int index = 0; index < corners.Length; index++)
            {
                Vector3 corner = corners[index];
                int xBit = index & 1;
                int yBit = (index >> 1) & 1;
                int zBit = (index >> 2) & 1;
                int normalBit = normalAxis == 0 ? xBit : normalAxis == 1 ? yBit : zBit;
                if ((sign > 0f && normalBit != 1) || (sign < 0f && normalBit != 0)) continue;
                Vector3 tangent = corner - localImpact;
                tangent -= localNormal * Vector3.Dot(tangent, localNormal);
                float distance = tangent.Length();
                if (distance >= radius) continue;
                float radial = 1f - distance / radius;
                float angle = MathF.Atan2(tangent.Y + tangent.Z * 0.37f,
                    tangent.X + tangent.Z * 0.61f);
                float facet = radial >= 0.58f
                    ? 1f
                    : radial <= 0.12f ? 0f : (radial - 0.12f) / 0.46f;
                facet *= 0.92f + 0.08f * MathF.Abs(MathF.Cos(angle * 3f + phase));
                Vector3 rest = new(cell.CellX + xBit, cell.CellY + yBit, cell.CellZ + zBit);
                float existingDepth = MathF.Max(0f, Vector3.Dot(rest - corner, localNormal));
                float applied = MathF.Min(increment * facet, MathF.Max(0f, 0.38f - existingDepth));
                if (applied <= 0.0001f) continue;
                corners[index] -= localNormal * applied;
                changed = true;
            }
            replacement.Add(cell with
            {
                Center = corners.Aggregate(Vector3.Zero, (sum, point) => sum + point) / corners.Length,
                ExplicitCorners = corners,
            });
        }
        if (!changed) return false;
        grid.SetSmoothSpheres(replacement);
        grid.FreezeAsStaticDeformation();
        _game!.World.PhysicsGrids.NotifyChanged();
        return true;
    }

    private static MaterialResponse ResponseFor(BlockDefinition material)
    {
        bool metal = material.Metallic >= 0.55f || HasTag(material, "metal");
        bool soil = HasTag(material, "soil") || HasTag(material, "grass") ||
                    HasTag(material, "dirt") || HasTag(material, "sand");
        bool elastic = HasTag(material, "soft-body") || HasTag(material, "gel") || HasTag(material, "foam");
        if (metal)
            return new MaterialResponse(DentShape.FacetedMetal,
                // A focused High pulse (36 force at the default 1.25 radius) is
                // about 7.3 pressure.  The old 14+ threshold made every ordinary
                // metal effectively indestructible even though the tool UI promised
                // deformation. Medium stays below this yield point.
                4.4f + material.Density * 0.18f + material.HardnessSeconds * 0.65f, 0.42f);
        if (soil || elastic)
            return new MaterialResponse(DentShape.SmoothBowl,
                MathF.Max(1.1f, material.HardnessSeconds * 1.8f), elastic ? 0.68f : 0.54f);
        return new MaterialResponse(DentShape.Chipped,
            4f + material.HardnessSeconds * 3.2f, 0.34f);
    }

    private static bool HasTag(BlockDefinition block, string tag) =>
        block.Tags.Any(value => value.Contains(tag, StringComparison.OrdinalIgnoreCase));

    private static bool IsGlass(BlockDefinition block) => block.IsTransparent &&
        (HasTag(block, "glass") || block.Key.Contains("glass", StringComparison.OrdinalIgnoreCase) ||
         block.DisplayName.Contains("Glass", StringComparison.OrdinalIgnoreCase) ||
         block.DisplayName.Contains("玻璃", StringComparison.Ordinal));

    private static float DistanceToImpactPlane(Vector3 offset, Vector3 normal)
    {
        Vector3 tangent = offset - normal * Vector3.Dot(offset, normal);
        return tangent.Length();
    }

    private static int DominantAxis(Vector3 normal)
    {
        Vector3 value = Vector3.Abs(normal);
        return value.X >= value.Y && value.X >= value.Z ? 0 : value.Y >= value.Z ? 1 : 2;
    }

    private static float Axis(Vector3 value, int axis) => axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;
    private static (float U, float V) Project(Vector3 value, int axis) => axis switch
    {
        0 => (value.Z, value.Y),
        1 => (value.X, value.Z),
        _ => (value.X, value.Y),
    };

    private enum DentShape { SmoothBowl, FacetedMetal, Chipped }
    private readonly record struct MaterialResponse(DentShape Shape, float YieldPressure, float MaximumDepth);
    private readonly record struct MetalCell(
        int X,
        int Y,
        int Z,
        ushort MaterialId,
        MaterialResponse Response,
        float Strength);
}
