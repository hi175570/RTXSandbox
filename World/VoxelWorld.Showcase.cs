using System.Numerics;
using RTXSandbox.Engine;

namespace RTXSandbox.World;

public sealed partial class VoxelWorld
{
    private const int ShowcaseGroundY = 4;

    /// <summary>
    /// Builds the persistent RTX demonstration campus. The rooms deliberately share one
    /// world so every renderer sees identical geometry, materials, fluids and rigid bodies.
    /// </summary>
    private void BuildShowcaseFeatures()
    {
        BuildShowcasePlaza();
        BuildWaterAndRefractionLab();
        BuildGlassSpectrumGallery();
        BuildMirrorHall();
        BuildCameraObscura();
        BuildPbrMaterialCourt();
        BuildPhysicsArena();
        BuildMechanicsAndSoftBodyLab();
    }

    private void BuildShowcasePlaza()
    {
        ushort industrialFloor = ShowcaseBlock("rtxsandbox:industrial_floor", VoxelMaterial.Marble);
        ushort hazard = ShowcaseBlock("rtxsandbox:hazard_stripes", VoxelMaterial.Gold);
        ushort whiteLed = ShowcaseBlock("rtxsandbox:white_led", VoxelMaterial.Glow);

        FillShowcaseBox(24, ShowcaseGroundY, 23, 43, ShowcaseGroundY, 42, industrialFloor);
        FillShowcaseBox(29, ShowcaseGroundY, 27, 38, ShowcaseGroundY, 37, VoxelMaterial.WhiteConcrete);
        for (int x = 24; x <= 43; x++)
        {
            SetSilently(x, ShowcaseGroundY, 22, hazard);
            SetSilently(x, ShowcaseGroundY, 43, hazard);
        }
        for (int z = 23; z <= 42; z++)
        {
            SetSilently(23, ShowcaseGroundY, z, hazard);
            SetSilently(44, ShowcaseGroundY, z, hazard);
        }

        // Four bright pylons make the central spawn readable in every renderer.
        foreach ((int x, int z) in new[] { (26, 25), (41, 25), (26, 40), (41, 40) })
        {
            for (int y = 5; y <= 9; y++) SetSilently(x, y, z, VoxelMaterial.Marble);
            SetSilently(x, 10, z, whiteLed);
        }
    }

    private void BuildWaterAndRefractionLab()
    {
        ushort clearGlass = ShowcaseBlock("rtxsandbox:clear_glass", VoxelMaterial.Glass);
        ushort aqua = ShowcaseBlock("rtxsandbox:aqua_crystal", VoxelMaterial.Crystal);
        ushort cyan = ShowcaseBlock("rtxsandbox:cyan_neon", VoxelMaterial.BlueLamp);

        // A mirror-bottomed pool, stepped waterfall and submerged lights expose water
        // transparency, refraction, reflection, fluid height and underwater fog together.
        FillShowcaseBox(2, ShowcaseGroundY, 2, 21, ShowcaseGroundY, 21, VoxelMaterial.Marble);
        FillShowcaseBox(4, ShowcaseGroundY, 4, 19, ShowcaseGroundY, 19, VoxelMaterial.Mirror);
        for (int z = 3; z <= 20; z++)
        for (int x = 3; x <= 20; x++)
        {
            bool rim = x is 3 or 20 || z is 3 or 20;
            SetSilently(x, 5, z, rim ? clearGlass : (ushort)VoxelMaterial.Water);
        }
        for (int z = 6; z <= 18; z += 4)
        for (int x = 6; x <= 18; x += 4)
            SetSilently(x, ShowcaseGroundY, z, ((x + z) & 4) == 0 ? aqua : cyan);

        for (int step = 0; step < 6; step++)
        {
            int x = 3 + step;
            int y = 11 - step;
            for (int z = 9; z <= 13; z++)
            {
                SetSilently(x, y - 1, z, VoxelMaterial.Marble);
                SetSilently(x, y, z, VoxelMaterial.Water);
            }
        }
        FillShowcaseBox(2, 5, 8, 2, 12, 14, VoxelMaterial.Marble);
        for (int z = 9; z <= 13; z++) SetSilently(3, 12, z, VoxelMaterial.Water);
    }

    private void BuildGlassSpectrumGallery()
    {
        ushort[] glass =
        {
            ShowcaseBlock("rtxsandbox:clear_glass", VoxelMaterial.Glass),
            ShowcaseBlock("rtxsandbox:red_glass", VoxelMaterial.Glass),
            ShowcaseBlock("rtxsandbox:green_glass", VoxelMaterial.Glass),
            ShowcaseBlock("rtxsandbox:blue_glass", VoxelMaterial.Glass),
            ShowcaseBlock("rtxsandbox:yellow_glass", VoxelMaterial.Glass),
            ShowcaseBlock("rtxsandbox:purple_glass", VoxelMaterial.Glass),
            ShowcaseBlock("rtxsandbox:diamond_glass", VoxelMaterial.Crystal),
            ShowcaseBlock("rtxsandbox:prismatic_glass", VoxelMaterial.Crystal),
            ShowcaseBlock("rtxsandbox:frosted_glass", VoxelMaterial.FrostedGlass),
            ShowcaseBlock("rtxsandbox:mosaic_glass", VoxelMaterial.FrostedGlass),
            ShowcaseBlock("rtxsandbox:ribbed_glass", VoxelMaterial.FrostedGlass),
            ShowcaseBlock("rtxsandbox:etched_glass", VoxelMaterial.FrostedGlass),
        };
        ushort[] lamps =
        {
            ShowcaseBlock("rtxsandbox:warm_led", VoxelMaterial.RedLamp),
            ShowcaseBlock("rtxsandbox:green_neon", VoxelMaterial.Glow),
            ShowcaseBlock("rtxsandbox:cyan_neon", VoxelMaterial.BlueLamp),
            ShowcaseBlock("rtxsandbox:magenta_neon", VoxelMaterial.PurpleLamp),
        };

        FillShowcaseBox(23, ShowcaseGroundY, 2, 43, ShowcaseGroundY, 20, VoxelMaterial.WhiteConcrete);
        for (int index = 0; index < glass.Length; index++)
        {
            int column = index % 7;
            int row = index / 7;
            int x = 24 + column * 3;
            int z = 5 + row * 9;
            FillShowcaseBox(x, 5, z, x + 1, 11, z, glass[index]);
            SetSilently(x, 7, z + 2, lamps[index % lamps.Length]);
            SetSilently(x + 1, 7, z + 2, lamps[(index + 1) % lamps.Length]);
        }

        // A compact dispersive prism sculpture surrounded by white emitters.
        ushort prism = ShowcaseBlock("rtxsandbox:prismatic_glass", VoxelMaterial.Crystal);
        ushort whiteLed = ShowcaseBlock("rtxsandbox:white_led", VoxelMaterial.Glow);
        for (int layer = 0; layer < 6; layer++)
        for (int offset = -layer; offset <= layer; offset++)
            SetSilently(40 + offset, 5 + layer, 18, prism);
        SetSilently(32, 8, 18, whiteLed);
    }

    private void BuildMirrorHall()
    {
        ushort perfectMirror = ShowcaseBlock("rtxsandbox:perfect_mirror", VoxelMaterial.Mirror);
        ushort absorber = ShowcaseBlock("rtxsandbox:light_absorber", VoxelMaterial.BlackConcrete);
        ushort plasma = ShowcaseBlock("rtxsandbox:plasma_block", VoxelMaterial.PurpleLamp);
        ushort rainbow = ShowcaseBlock("rtxsandbox:rainbow_block", VoxelMaterial.Gold);

        FillShowcaseBox(45, ShowcaseGroundY, 2, 62, ShowcaseGroundY, 25, absorber);
        for (int z = 4; z <= 23; z++)
        for (int y = 5; y <= 15; y++)
        {
            SetSilently(47, y, z, perfectMirror);
            SetSilently(60, y, z, perfectMirror);
        }
        FillShowcaseBox(47, 16, 4, 60, 16, 23, perfectMirror);
        for (int z = 6; z <= 21; z += 3)
        {
            SetSilently(53, 5, z, (z & 1) == 0 ? plasma : rainbow);
            SetSilently(54, 5, z, (z & 1) == 0 ? rainbow : plasma);
        }

        AnalyticSphere mirrorSphere = AnalyticSpheres.Create(new Vector3(53.5f, 7.25f, 9.5f), 1.2f,
            ShowcaseBlock("rtxsandbox:mirror_sphere", VoxelMaterial.Metal));
        mirrorSphere.LinearVelocity = new Vector3(0f, 0f, 0.35f);
        AnalyticSpheres.Create(new Vector3(54.5f, 8.4f, 18.5f), 1.45f,
            ShowcaseBlock("rtxsandbox:iron_sphere", VoxelMaterial.Metal));
    }

    private void BuildCameraObscura()
    {
        ushort absorber = ShowcaseBlock("rtxsandbox:light_absorber", VoxelMaterial.BlackConcrete);
        ushort screen = ShowcaseBlock("rtxsandbox:white_concrete", VoxelMaterial.WhiteConcrete);
        ushort red = ShowcaseBlock("rtxsandbox:red_lamp", VoxelMaterial.RedLamp);
        ushort cyan = ShowcaseBlock("rtxsandbox:cyan_neon", VoxelMaterial.BlueLamp);
        ushort green = ShowcaseBlock("rtxsandbox:green_neon", VoxelMaterial.Glow);
        ushort volume = ShowcaseBlock("rtxsandbox:volumetric_light", VoxelMaterial.Glass);

        // Closed camera-obscura shell: the east wall has exactly one one-block aperture.
        FillShowcaseBox(2, ShowcaseGroundY, 27, 21, ShowcaseGroundY, 55, absorber);
        FillShowcaseBox(2, 16, 27, 21, 16, 55, absorber);
        for (int y = 5; y <= 15; y++)
        for (int z = 27; z <= 55; z++)
        {
            SetSilently(2, y, z, absorber);
            SetSilently(21, y, z, absorber);
        }
        for (int y = 5; y <= 15; y++)
        for (int x = 2; x <= 21; x++)
        {
            SetSilently(x, y, 27, absorber);
            SetSilently(x, y, 55, absorber);
        }
        FillShowcaseBox(3, 6, 31, 3, 14, 49, screen);
        SetSilently(21, 10, 41, VoxelMaterial.Air);

        // The external 5x5 RGB arrow is projected through (21.5,10.5,41.5).
        // The receiving pixels are generated by the same central-projection equation at
        // 2x magnification, which necessarily flips both axes instead of hand-copying art.
        var sourcePixels = new List<(int Y, int Z, ushort Material)>();
        for (int y = 8; y <= 12; y++) sourcePixels.Add((y, 41, red));
        sourcePixels.Add((12, 39, cyan));
        sourcePixels.Add((12, 40, cyan));
        sourcePixels.Add((12, 42, green));
        sourcePixels.Add((12, 43, green));
        sourcePixels.Add((10, 39, cyan));
        sourcePixels.Add((10, 43, green));
        foreach ((int y, int z, ushort material) in sourcePixels)
        {
            SetSilently(27, y, z, material);
            int projectedY = 10 - (y - 10) * 2;
            int projectedZ = 41 - (z - 41) * 2;
            SetSilently(3, projectedY, projectedZ, material);
        }
        FillShowcaseBox(27, 7, 37, 27, 13, 45, ShowcaseBlock("rtxsandbox:carbon_panel", VoxelMaterial.BlackConcrete));
        foreach ((int y, int z, ushort material) in sourcePixels) SetSilently(27, y, z, material);

        // Sparse non-colliding ray markers reveal the source -> aperture -> inverted-image
        // geometry without closing the aperture or becoming a second opaque wall.
        foreach ((int targetY, int targetZ) in new[] { (6, 41), (14, 41), (10, 37), (10, 45) })
        {
            for (int x = 6; x <= 18; x += 4)
            {
                float t = (21f - x) / 18f;
                int y = (int)MathF.Round(10f + (targetY - 10f) * t);
                int z = (int)MathF.Round(41f + (targetZ - 41f) * t);
                SetSilently(x, y, z, volume);
            }
        }
    }

    private void BuildPbrMaterialCourt()
    {
        ushort[] metals =
        {
            ShowcaseBlock("rtxsandbox:iron_block", VoxelMaterial.Metal),
            ShowcaseBlock("rtxsandbox:silver_block", VoxelMaterial.Metal),
            ShowcaseBlock("rtxsandbox:aluminum_block", VoxelMaterial.Metal),
            ShowcaseBlock("rtxsandbox:steel_block", VoxelMaterial.Metal),
            ShowcaseBlock("rtxsandbox:stainless_steel", VoxelMaterial.Metal),
            ShowcaseBlock("rtxsandbox:titanium_alloy", VoxelMaterial.Metal),
            ShowcaseBlock("rtxsandbox:bronze_block", VoxelMaterial.Copper),
            ShowcaseBlock("rtxsandbox:brass_block", VoxelMaterial.Gold),
            ShowcaseBlock("rtxsandbox:chrome_block", VoxelMaterial.Mirror),
            ShowcaseBlock("rtxsandbox:oxidized_copper", VoxelMaterial.Copper),
            ShowcaseBlock("rtxsandbox:rusted_steel", VoxelMaterial.Metal),
        };
        FillShowcaseBox(24, ShowcaseGroundY, 45, 43, ShowcaseGroundY, 62, VoxelMaterial.Marble);
        for (int index = 0; index < metals.Length; index++)
        {
            int x = 25 + (index % 6) * 3;
            int z = 48 + (index / 6) * 7;
            int height = 2 + index % 4;
            FillShowcaseBox(x, 5, z, x + 1, 4 + height, z + 1, metals[index]);
        }
        ushort whiteLed = ShowcaseBlock("rtxsandbox:white_led", VoxelMaterial.Glow);
        for (int x = 25; x <= 40; x += 5) SetSilently(x, 11, 46, whiteLed);
    }

    private void BuildPhysicsArena()
    {
        ushort floor = ShowcaseBlock("rtxsandbox:industrial_floor", VoxelMaterial.Metal);
        ushort hazard = ShowcaseBlock("rtxsandbox:hazard_stripes", VoxelMaterial.Gold);
        ushort iron = ShowcaseBlock("rtxsandbox:iron_block", VoxelMaterial.Metal);
        ushort glass = ShowcaseBlock("rtxsandbox:acrylic_glass", VoxelMaterial.Glass);
        ushort energy = ShowcaseBlock("rtxsandbox:energy_core", VoxelMaterial.BlueLamp);

        FillShowcaseBox(45, ShowcaseGroundY, 28, 62, ShowcaseGroundY, 62, floor);
        for (int z = 28; z <= 62; z++)
        {
            SetSilently(45, ShowcaseGroundY + 1, z, hazard);
            SetSilently(62, ShowcaseGroundY + 1, z, hazard);
        }

        // A falling glass-and-steel bridge: one connected body so it can translate and
        // rotate as a structure while every cell remains individually destructible.
        var bridge = new List<PhysicsGridElement>();
        for (int x = 0; x < 12; x++)
        {
            ushort material = x is 0 or 11 || x % 3 == 0 ? iron : glass;
            bridge.Add(ShowcaseGridCell(x, 0, 0, material));
            if (x % 4 == 0) bridge.Add(ShowcaseGridCell(x, 1, 0, energy));
        }
        PhysicsGrid bridgeGrid = PhysicsGrids.Create(bridge, new Vector3(48f, 12f, 34f), ShowcaseDensity);
        bridgeGrid.AngularVelocity = new Vector3(0f, 0f, 0.16f);
        bridgeGrid.FractureOnImpact = true;

        // A domino line uses separate rigid structures so impacts propagate rather than
        // being rendered as one immovable decorative model.
        for (int domino = 0; domino < 7; domino++)
        {
            var cells = new List<PhysicsGridElement>();
            for (int y = 0; y < 4; y++) cells.Add(ShowcaseGridCell(0, y, 0, iron));
            PhysicsGrid grid = PhysicsGrids.Create(cells, new Vector3(48f + domino * 1.8f, 5f, 46f), ShowcaseDensity);
            if (domino == 0) grid.AngularVelocity = new Vector3(0f, 0f, -0.55f);
        }

        // Pushable analytic spheres exercise exact sphere reflection plus rigid collision.
        for (int index = 0; index < 5; index++)
        {
            ushort material = index % 2 == 0
                ? ShowcaseBlock("rtxsandbox:mirror_sphere", VoxelMaterial.Metal)
                : ShowcaseBlock("rtxsandbox:iron_sphere", VoxelMaterial.Metal);
            AnalyticSphere sphere = AnalyticSpheres.Create(
                new Vector3(49.5f + index * 2.4f, 7f + index * 0.35f, 56f),
                0.65f + index * 0.08f,
                material);
            sphere.LinearVelocity = new Vector3(index % 2 == 0 ? 0.4f : -0.25f, 0f, 0f);
        }
    }

    private void BuildMechanicsAndSoftBodyLab()
    {
        ushort gel = ShowcaseBlock("rtxsandbox:gel", VoxelMaterial.Crystal);
        ushort rubber = ShowcaseBlock("rtxsandbox:rubber", VoxelMaterial.RedLamp);
        ushort foam = ShowcaseBlock("rtxsandbox:foam", VoxelMaterial.Gold);
        ushort iron = ShowcaseBlock("rtxsandbox:iron_block", VoxelMaterial.Metal);
        ushort controller = ShowcaseBlock("rtxsandbox:animation_controller", VoxelMaterial.Metal);

        // These beams are authored as ordinary world voxels attached to one continuous
        // fixture wall. They must not be serialized as already-detached PhysicsGrids:
        // the runtime soft-body extractor owns the world-to-grid transition and records
        // the wall contact as a pinned support before deformation begins.
        FillShowcaseBox(46, 5, 51, 46, 13, 61, VoxelMaterial.Bedrock);
        for (int x = 47; x <= 52; x++)
            SetSilently(x, 11, 60, x == 52 ? iron : gel);

        // Three independent softness profiles are deliberately adjacent for direct
        // comparison and for water/physics impact tests.
        foreach ((ushort material, int z) in new[] { (gel, 52), (rubber, 54), (foam, 56) })
        {
            for (int x = 47; x <= 50; x++) SetSilently(x, 9, z, material);
            SetSilently(51, 9, z, iron);
        }

        // Mechanical prototypes were removed. Keep one ordinary anchored controller
        // block as the future right-click trajectory editor entry point; it owns no
        // scripted motion yet and therefore cannot make nearby structures hover.
        FillShowcaseBox(25, 5, 49, 29, 5, 49, VoxelMaterial.Bedrock);
        SetSilently(27, 6, 49, controller);
    }

    private ushort ShowcaseBlock(string key, VoxelMaterial fallback) =>
        _blocks.TryGet(key, out BlockDefinition? definition) ? definition!.Id : (ushort)fallback;

    private float ShowcaseDensity(ushort materialId) => _blocks.Get(materialId).Density;

    private static PhysicsGridElement ShowcaseGridCell(int x, int y, int z, ushort material) =>
        new(x, y, z, new Vector3(x, y, z), new Vector3(x + 1f, y + 1f, z + 1f), material);

    private void FillShowcaseBox(
        int minimumX,
        int minimumY,
        int minimumZ,
        int maximumX,
        int maximumY,
        int maximumZ,
        VoxelMaterial material) =>
        FillShowcaseBox(minimumX, minimumY, minimumZ, maximumX, maximumY, maximumZ, (ushort)material);

    private void FillShowcaseBox(
        int minimumX,
        int minimumY,
        int minimumZ,
        int maximumX,
        int maximumY,
        int maximumZ,
        ushort material)
    {
        for (int z = minimumZ; z <= maximumZ; z++)
        for (int y = minimumY; y <= maximumY; y++)
        for (int x = minimumX; x <= maximumX; x++)
            SetSilently(x, y, z, material);
    }
}
