using System.Numerics;
using RTXSandbox.Engine;
using RTXSandbox.World;

namespace RTXSandbox.Rendering;

public enum MechanicalAxis { X, Y, Z }

public readonly record struct MechanicalPartBox(Vector3 Minimum, Vector3 Maximum);

public readonly record struct MechanicalPartMeshVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector2 Uv,
    int AtlasFace);

/// <summary>
/// Shared procedural silhouettes for mechanical blocks. These are deliberately
/// original low-poly parts: exposed shafts, collars, housings, spokes and teeth.
/// Raster and DXR consume the same boxes, so a machine is no longer six textured
/// faces pretending to be a mechanism.
/// </summary>
public static class MechanicalPartGeometry
{
    public static bool IsMechanical(BlockDefinition definition) =>
        definition.Tags.Any(tag =>
            tag.Equals("mechanical", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("motion-controller", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("mechanical-source", StringComparison.OrdinalIgnoreCase));

    public static MechanicalAxis ResolveAxis(
        int x,
        int y,
        int z,
        Func<int, int, int, ushort> materialAt,
        IReadOnlyList<BlockDefinition> definitions)
    {
        int MechanicalScore(int dx, int dy, int dz)
        {
            bool negative = IsMechanicalId(materialAt(x - dx, y - dy, z - dz), definitions);
            bool positive = IsMechanicalId(materialAt(x + dx, y + dy, z + dz), definitions);
            return (negative ? 1 : 0) + (positive ? 1 : 0) + (negative && positive ? 3 : 0);
        }

        int SupportScore(int dx, int dy, int dz)
        {
            ushort negativeId = materialAt(x - dx, y - dy, z - dz);
            ushort positiveId = materialAt(x + dx, y + dy, z + dz);
            bool negative = negativeId != 0 && !IsMechanicalId(negativeId, definitions);
            bool positive = positiveId != 0 && !IsMechanicalId(positiveId, definitions);
            return (negative ? 1 : 0) + (positive ? 1 : 0);
        }

        // A mounting face is authoritative.  Mechanical neighbours must not turn a
        // gear after a second gear is placed beside it; that made coplanar gear trains
        // impossible and visually detached the teeth from their shafts.
        int xSupport = SupportScore(1, 0, 0);
        int ySupport = SupportScore(0, 1, 0);
        int zSupport = SupportScore(0, 0, 1);
        if (xSupport > 0 || zSupport > 0)
            return zSupport > xSupport ? MechanicalAxis.Z : MechanicalAxis.X;
        if (ySupport > 0) return MechanicalAxis.Y;

        int xScore = MechanicalScore(1, 0, 0);
        int yScore = MechanicalScore(0, 1, 0);
        int zScore = MechanicalScore(0, 0, 1);
        if (yScore > xScore && yScore >= zScore) return MechanicalAxis.Y;
        return zScore > xScore ? MechanicalAxis.Z : MechanicalAxis.X;
    }

    /// <summary>
    /// Builds the shared high precision visual mesh.  The old box list remains the
    /// conservative collision/compute fallback; raster, DXR and inventory previews
    /// use this triangle stream so teeth, hubs and shafts are genuinely connected.
    /// </summary>
    public static IReadOnlyList<MechanicalPartMeshVertex> BuildVisualMesh(
        BlockDefinition definition,
        MechanicalAxis axis)
    {
        var vertices = new List<MechanicalPartMeshVertex>(1_800);
        bool source = HasTag(definition, "mechanical-source");
        bool gearbox = HasTag(definition, "gearbox");
        bool flywheel = HasTag(definition, "flywheel");
        bool gear = HasTag(definition, "gear");
        bool shaft = HasTag(definition, "shaft");
        bool pulley = HasTag(definition, "pulley");
        bool coupler = HasTag(definition, "coupler");
        bool clutch = HasTag(definition, "clutch");

        if (source)
        {
            AddCylinder(vertices, axis, 0.08f, 0.78f, 0.31f, 40);
            AddCylinder(vertices, axis, 0.02f, 0.11f, 0.22f, 32);
            AddCylinder(vertices, axis, 0.72f, 1.00f, 0.085f, 32);
            for (int rib = 0; rib < 6; rib++)
            {
                float x0 = 0.13f + rib * 0.095f;
                AddCylinder(vertices, axis, x0, x0 + 0.025f, 0.345f, 40);
            }
            return vertices;
        }

        if (gearbox)
        {
            foreach (MechanicalPartBox box in Build(definition, axis)) AddBoxMesh(vertices, box);
            return vertices;
        }

        if (gear || flywheel)
        {
            bool large = HasTag(definition, "large-gear") || flywheel;
            bool small = HasTag(definition, "small-gear");
            bool bevel = HasTag(definition, "bevel-gear");
            bool sprocket = HasTag(definition, "sprocket");
            float radius = large ? 0.40f : small ? 0.275f : bevel ? 0.355f : 0.335f;
            int teeth = large ? 20 : small ? 12 : sprocket ? 16 : 16;
            float thickness = flywheel ? 0.15f : bevel ? 0.24f : 0.18f;
            AddCylinder(vertices, axis, 0f, 1f, 0.075f, 32);
            AddGear(vertices, axis, 0.50f, radius, teeth, thickness,
                sprocket ? 0.035f : 0.055f, flywheel ? 8 : 6);
            if (bevel)
                AddGear(vertices, axis, 0.60f, radius * 0.72f, Math.Max(10, teeth - 4),
                    0.11f, 0.035f, 5);
            return vertices;
        }

        if (shaft)
        {
            AddCylinder(vertices, axis, 0f, 1f, 0.105f, 32);
            AddCylinder(vertices, axis, 0.07f, 0.18f, 0.19f, 32);
            AddCylinder(vertices, axis, 0.82f, 0.93f, 0.19f, 32);
            return vertices;
        }

        if (pulley || coupler || clutch)
        {
            AddCylinder(vertices, axis, 0f, 1f, 0.075f, 32);
            AddCylinder(vertices, axis, 0.35f, 0.65f, pulley ? 0.35f : 0.29f, 40);
            if (pulley)
            {
                AddCylinder(vertices, axis, 0.32f, 0.39f, 0.39f, 40);
                AddCylinder(vertices, axis, 0.61f, 0.68f, 0.39f, 40);
            }
            return vertices;
        }

        foreach (MechanicalPartBox box in Build(definition, axis)) AddBoxMesh(vertices, box);
        return vertices;
    }

    public static IReadOnlyList<MechanicalPartBox> Build(BlockDefinition definition, MechanicalAxis axis)
    {
        var parts = new List<MechanicalPartBox>(28);
        bool source = HasTag(definition, "mechanical-source");
        bool gearbox = HasTag(definition, "gearbox");
        bool flywheel = HasTag(definition, "flywheel");
        bool shaft = HasTag(definition, "shaft");
        bool coupler = HasTag(definition, "coupler");
        bool brake = HasTag(definition, "brake");
        bool gear = HasTag(definition, "gear");
        bool smallGear = HasTag(definition, "small-gear");
        bool largeGear = HasTag(definition, "large-gear");
        bool bevelGear = HasTag(definition, "bevel-gear");
        bool rack = HasTag(definition, "gear-rack");
        bool universalJoint = HasTag(definition, "universal-joint");
        bool clutch = HasTag(definition, "clutch");
        bool pulley = HasTag(definition, "pulley");
        bool sprocket = HasTag(definition, "sprocket");
        bool piston = HasTag(definition, "linear") || HasTag(definition, "lift") ||
                      HasTag(definition, "impulse-pad");
        bool rotary = HasTag(definition, "rotary") || HasTag(definition, "rocker");
        bool conveyor = HasTag(definition, "conveyor");

        if (source)
        {
            Add(parts, axis, new(0.05f, 0.17f, 0.17f), new(0.78f, 0.83f, 0.83f));
            for (int band = 0; band < 4; band++)
            {
                float x = 0.12f + band * 0.17f;
                Add(parts, axis, new(x, 0.11f, 0.11f), new(x + 0.045f, 0.89f, 0.89f));
            }
            AddShaft(parts, axis, 0.72f, 1f, 0.14f);
            Add(parts, axis, new(0.02f, 0.34f, 0.34f), new(0.10f, 0.66f, 0.66f));
        }
        else if (gearbox)
        {
            Add(parts, axis, new(0.12f, 0.13f, 0.13f), new(0.88f, 0.87f, 0.87f));
            AddShaft(parts, axis, 0f, 1f, 0.105f);
            AddCog(parts, axis, 0.29f, 0.31f, 10);
            AddCog(parts, axis, 0.69f, 0.25f, 8);
        }
        else if (flywheel)
        {
            AddShaft(parts, axis, 0f, 1f, 0.105f);
            AddCog(parts, axis, 0.50f, 0.40f, 16, 0.16f);
            AddSpokes(parts, axis, 0.50f);
        }
        else if (piston)
        {
            Add(parts, axis, new(0.03f, 0.12f, 0.12f), new(0.48f, 0.88f, 0.88f));
            AddShaft(parts, axis, 0.42f, 0.94f, 0.105f);
            Add(parts, axis, new(0.88f, 0.22f, 0.22f), new(0.98f, 0.78f, 0.78f));
            Add(parts, axis, new(0.10f, 0.06f, 0.06f), new(0.20f, 0.94f, 0.94f));
        }
        else if (rotary || coupler)
        {
            AddShaft(parts, axis, 0f, 1f, 0.12f);
            Add(parts, axis, new(0.31f, 0.14f, 0.14f), new(0.69f, 0.86f, 0.86f));
            AddCog(parts, axis, 0.50f, rotary ? 0.34f : 0.28f, rotary ? 12 : 8, 0.12f);
        }
        else if (conveyor)
        {
            Add(parts, axis, new(0.03f, 0.20f, 0.20f), new(0.97f, 0.80f, 0.80f));
            AddShaft(parts, axis, 0f, 1f, 0.10f);
            Add(parts, axis, new(0.10f, 0.10f, 0.10f), new(0.28f, 0.90f, 0.90f));
            Add(parts, axis, new(0.72f, 0.10f, 0.10f), new(0.90f, 0.90f, 0.90f));
        }
        else if (brake)
        {
            AddShaft(parts, axis, 0f, 1f, 0.11f);
            AddCog(parts, axis, 0.50f, 0.35f, 12, 0.15f);
            Add(parts, axis, new(0.30f, 0.08f, 0.36f), new(0.70f, 0.25f, 0.64f));
            Add(parts, axis, new(0.30f, 0.75f, 0.36f), new(0.70f, 0.92f, 0.64f));
        }
        else if (rack)
        {
            Add(parts, axis, new(0.02f, 0.36f, 0.34f), new(0.98f, 0.64f, 0.66f));
            for (int tooth = 0; tooth < 9; tooth++)
            {
                float x = 0.055f + tooth * 0.11f;
                Add(parts, axis, new(x, 0.63f, 0.38f), new(x + 0.06f, 0.82f, 0.62f));
            }
        }
        else if (universalJoint)
        {
            AddShaft(parts, axis, 0f, 0.40f, 0.09f);
            AddShaft(parts, axis, 0.60f, 1f, 0.09f);
            Add(parts, axis, new(0.36f, 0.18f, 0.44f), new(0.64f, 0.82f, 0.56f));
            Add(parts, axis, new(0.44f, 0.36f, 0.18f), new(0.56f, 0.64f, 0.82f));
        }
        else if (clutch)
        {
            AddShaft(parts, axis, 0f, 1f, 0.095f);
            AddCog(parts, axis, 0.39f, 0.30f, 12, 0.13f);
            AddCog(parts, axis, 0.61f, 0.30f, 12, 0.13f);
        }
        else if (pulley)
        {
            AddShaft(parts, axis, 0f, 1f, 0.09f);
            AddCog(parts, axis, 0.50f, 0.34f, 16, 0.10f);
            AddCog(parts, axis, 0.50f, 0.27f, 16, 0.18f);
        }
        else if (gear)
        {
            AddShaft(parts, axis, 0f, 1f, 0.09f);
            float radius = largeGear ? 0.41f : smallGear ? 0.27f : bevelGear ? 0.35f : 0.33f;
            int teeth = largeGear ? 18 : smallGear ? 10 : sprocket ? 14 : 12;
            AddCog(parts, axis, 0.50f, radius, teeth, bevelGear ? 0.28f : 0.18f);
            if (bevelGear)
                AddCog(parts, axis, 0.62f, radius * 0.72f, Math.Max(8, teeth - 4), 0.12f);
        }
        else if (shaft)
        {
            AddShaft(parts, axis, 0f, 1f, 0.11f);
            Add(parts, axis, new(0.08f, 0.29f, 0.29f), new(0.17f, 0.71f, 0.71f));
            Add(parts, axis, new(0.83f, 0.29f, 0.29f), new(0.92f, 0.71f, 0.71f));
        }
        else
        {
            AddShaft(parts, axis, 0f, 1f, 0.11f);
            AddCog(parts, axis, 0.50f, 0.34f, 12);
        }
        return parts;
    }

    private static void AddShaft(List<MechanicalPartBox> parts, MechanicalAxis axis,
        float minimum, float maximum, float halfWidth) =>
        Add(parts, axis,
            new Vector3(minimum, 0.5f - halfWidth, 0.5f - halfWidth),
            new Vector3(maximum, 0.5f + halfWidth, 0.5f + halfWidth));

    private static void AddCog(List<MechanicalPartBox> parts, MechanicalAxis axis,
        float centerX, float radius, int teeth, float thickness = 0.20f)
    {
        // Conservative box fallback for compute path tracing and collision.  Keep a
        // continuous root ring and spokes; the previous implementation placed only
        // isolated cubes at tooth tips, visibly floating away from the hub.
        Add(parts, axis,
            new Vector3(centerX - thickness * 0.58f, 0.40f, 0.40f),
            new Vector3(centerX + thickness * 0.58f, 0.60f, 0.60f));
        float spokeHalf = MathF.Max(0.035f, radius * 0.10f);
        Add(parts, axis,
            new Vector3(centerX - thickness * 0.50f, 0.5f - spokeHalf, 0.5f - radius * 0.78f),
            new Vector3(centerX + thickness * 0.50f, 0.5f + spokeHalf, 0.5f + radius * 0.78f));
        Add(parts, axis,
            new Vector3(centerX - thickness * 0.50f, 0.5f - radius * 0.78f, 0.5f - spokeHalf),
            new Vector3(centerX + thickness * 0.50f, 0.5f + radius * 0.78f, 0.5f + spokeHalf));
        float rootRadius = radius * 0.82f;
        int rootSegments = teeth * 2;
        float rootHalf = MathF.Max(0.045f, rootRadius * MathF.PI / rootSegments * 0.92f);
        for (int segment = 0; segment < rootSegments; segment++)
        {
            float angle = segment * MathF.Tau / rootSegments;
            float y = 0.5f + MathF.Cos(angle) * rootRadius;
            float z = 0.5f + MathF.Sin(angle) * rootRadius;
            Add(parts, axis,
                new Vector3(centerX - thickness * 0.50f, y - rootHalf, z - rootHalf),
                new Vector3(centerX + thickness * 0.50f, y + rootHalf, z + rootHalf));
        }
        float toothRadius = radius + 0.045f;
        for (int tooth = 0; tooth < teeth; tooth++)
        {
            float angle = tooth * MathF.Tau / teeth;
            float y = 0.5f + MathF.Cos(angle) * toothRadius;
            float z = 0.5f + MathF.Sin(angle) * toothRadius;
            float tangential = MathF.Max(0.055f, radius * MathF.PI / teeth * 0.72f);
            Add(parts, axis,
                new Vector3(centerX - thickness * 0.62f, y - tangential, z - tangential),
                new Vector3(centerX + thickness * 0.62f, y + tangential, z + tangential));
        }
    }

    private static void AddSpokes(List<MechanicalPartBox> parts, MechanicalAxis axis, float centerX)
    {
        Add(parts, axis, new(centerX - 0.10f, 0.46f, 0.14f), new(centerX + 0.10f, 0.54f, 0.86f));
        Add(parts, axis, new(centerX - 0.10f, 0.14f, 0.46f), new(centerX + 0.10f, 0.86f, 0.54f));
    }

    private static void AddGear(
        List<MechanicalPartMeshVertex> vertices,
        MechanicalAxis axis,
        float centerX,
        float pitchRadius,
        int teeth,
        float thickness,
        float toothDepth,
        int spokeCount)
    {
        float rootRadius = MathF.Max(0.16f, pitchRadius - toothDepth * 0.42f);
        float tipRadius = MathF.Min(0.485f, pitchRadius + toothDepth);
        float ringInner = MathF.Max(0.15f, rootRadius * 0.58f);
        float hubRadius = MathF.Min(ringInner * 0.62f, 0.14f);
        float x0 = centerX - thickness * 0.5f;
        float x1 = centerX + thickness * 0.5f;
        const int samplesPerTooth = 6;
        int samples = teeth * samplesPerTooth;
        var outline = new Vector2[samples];
        for (int index = 0; index < samples; index++)
        {
            int phase = index % samplesPerTooth;
            float radius = phase switch
            {
                0 or 5 => rootRadius,
                1 or 4 => pitchRadius,
                _ => tipRadius,
            };
            float angle = index * MathF.Tau / samples;
            outline[index] = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
        }

        for (int index = 0; index < samples; index++)
        {
            int next = (index + 1) % samples;
            float angle0 = index * MathF.Tau / samples;
            float angle1 = next * MathF.Tau / samples;
            Vector2 inner0 = new(MathF.Cos(angle0) * ringInner, MathF.Sin(angle0) * ringInner);
            Vector2 inner1 = new(MathF.Cos(angle1) * ringInner, MathF.Sin(angle1) * ringInner);
            Vector2 outer0 = outline[index];
            Vector2 outer1 = outline[next];

            AddQuad(vertices, axis,
                Point(x1, inner0), Point(x1, outer0), Point(x1, outer1), Point(x1, inner1), 1);
            AddQuad(vertices, axis,
                Point(x0, inner1), Point(x0, outer1), Point(x0, outer0), Point(x0, inner0), 1);
            AddQuad(vertices, axis,
                Point(x0, outer0), Point(x0, outer1), Point(x1, outer1), Point(x1, outer0), 1);
            AddQuad(vertices, axis,
                Point(x0, inner1), Point(x0, inner0), Point(x1, inner0), Point(x1, inner1), 1);
        }

        AddCylinder(vertices, axis, x0 - 0.025f, x1 + 0.025f, hubRadius, 32);
        for (int spoke = 0; spoke < spokeCount; spoke++)
        {
            float angle = spoke * MathF.Tau / spokeCount;
            float halfAngle = 0.055f / MathF.Max(ringInner, 0.1f);
            Vector2 a = Polar(hubRadius * 0.82f, angle - halfAngle);
            Vector2 b = Polar(ringInner * 1.04f, angle - halfAngle * 0.55f);
            Vector2 c = Polar(ringInner * 1.04f, angle + halfAngle * 0.55f);
            Vector2 d = Polar(hubRadius * 0.82f, angle + halfAngle);
            AddExtrudedLoop(vertices, axis, x0 + 0.006f, x1 - 0.006f, new[] { a, b, c, d });
        }

        static Vector3 Point(float x, Vector2 yz) => new(x, 0.5f + yz.X, 0.5f + yz.Y);
        static Vector2 Polar(float radius, float angle) =>
            new(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
    }

    private static void AddCylinder(
        List<MechanicalPartMeshVertex> vertices,
        MechanicalAxis axis,
        float minimum,
        float maximum,
        float radius,
        int segments)
    {
        for (int segment = 0; segment < segments; segment++)
        {
            float angle0 = segment * MathF.Tau / segments;
            float angle1 = (segment + 1) * MathF.Tau / segments;
            Vector3 p00 = CylinderPoint(minimum, radius, angle0);
            Vector3 p01 = CylinderPoint(minimum, radius, angle1);
            Vector3 p10 = CylinderPoint(maximum, radius, angle0);
            Vector3 p11 = CylinderPoint(maximum, radius, angle1);
            AddQuad(vertices, axis, p00, p01, p11, p10, 1);
            Vector3 centerMinimum = new(minimum, 0.5f, 0.5f);
            Vector3 centerMaximum = new(maximum, 0.5f, 0.5f);
            AddTriangle(vertices, axis, centerMinimum, p01, p00, 1);
            AddTriangle(vertices, axis, centerMaximum, p10, p11, 1);
        }

        static Vector3 CylinderPoint(float x, float radius, float angle) =>
            new(x, 0.5f + MathF.Cos(angle) * radius, 0.5f + MathF.Sin(angle) * radius);
    }

    private static void AddExtrudedLoop(
        List<MechanicalPartMeshVertex> vertices,
        MechanicalAxis axis,
        float minimum,
        float maximum,
        IReadOnlyList<Vector2> loop)
    {
        for (int index = 1; index < loop.Count - 1; index++)
        {
            AddTriangle(vertices, axis, Point(maximum, loop[0]), Point(maximum, loop[index]),
                Point(maximum, loop[index + 1]), 1);
            AddTriangle(vertices, axis, Point(minimum, loop[0]), Point(minimum, loop[index + 1]),
                Point(minimum, loop[index]), 1);
        }
        for (int index = 0; index < loop.Count; index++)
        {
            int next = (index + 1) % loop.Count;
            AddQuad(vertices, axis, Point(minimum, loop[index]), Point(minimum, loop[next]),
                Point(maximum, loop[next]), Point(maximum, loop[index]), 1);
        }

        static Vector3 Point(float x, Vector2 yz) => new(x, 0.5f + yz.X, 0.5f + yz.Y);
    }

    private static void AddBoxMesh(List<MechanicalPartMeshVertex> vertices, MechanicalPartBox box)
    {
        for (int face = 0; face < 6; face++)
        {
            Vector3 a = BoxCorner(box.Minimum, box.Maximum, face, 0);
            Vector3 b = BoxCorner(box.Minimum, box.Maximum, face, 1);
            Vector3 c = BoxCorner(box.Minimum, box.Maximum, face, 2);
            Vector3 d = BoxCorner(box.Minimum, box.Maximum, face, 3);
            int atlasFace = face == 0 ? 0 : face == 5 ? 2 : 1;
            AddQuadRaw(vertices, a, b, c, d, atlasFace);
        }
    }

    private static void AddQuad(
        List<MechanicalPartMeshVertex> vertices,
        MechanicalAxis axis,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 d,
        int atlasFace)
    {
        AddTriangle(vertices, axis, a, b, c, atlasFace);
        AddTriangle(vertices, axis, a, c, d, atlasFace);
    }

    private static void AddTriangle(
        List<MechanicalPartMeshVertex> vertices,
        MechanicalAxis axis,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        int atlasFace) =>
        AddTriangleRaw(vertices, Transform(a, axis), Transform(b, axis), Transform(c, axis), atlasFace);

    private static void AddQuadRaw(
        List<MechanicalPartMeshVertex> vertices,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        Vector3 d,
        int atlasFace)
    {
        AddTriangleRaw(vertices, a, b, c, atlasFace);
        AddTriangleRaw(vertices, a, c, d, atlasFace);
    }

    private static void AddTriangleRaw(
        List<MechanicalPartMeshVertex> vertices,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        int atlasFace)
    {
        Vector3 normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        if (!float.IsFinite(normal.X) || !float.IsFinite(normal.Y) || !float.IsFinite(normal.Z)) return;
        vertices.Add(new(a, normal, ProjectUv(a, normal), atlasFace));
        vertices.Add(new(b, normal, ProjectUv(b, normal), atlasFace));
        vertices.Add(new(c, normal, ProjectUv(c, normal), atlasFace));
    }

    private static Vector2 ProjectUv(Vector3 point, Vector3 normal)
    {
        Vector3 absolute = Vector3.Abs(normal);
        if (absolute.X >= absolute.Y && absolute.X >= absolute.Z) return new(point.Z, point.Y);
        if (absolute.Y >= absolute.Z) return new(point.X, point.Z);
        return new(point.X, point.Y);
    }

    private static Vector3 Transform(Vector3 value, MechanicalAxis axis) => axis switch
    {
        MechanicalAxis.Y => new(value.Y, value.X, value.Z),
        MechanicalAxis.Z => new(value.Y, value.Z, value.X),
        _ => value,
    };

    private static Vector3 BoxCorner(Vector3 minimum, Vector3 maximum, int face, int corner) =>
        (face, corner) switch
        {
            (0, 0) => new(minimum.X, maximum.Y, minimum.Z),
            (0, 1) => new(maximum.X, maximum.Y, minimum.Z),
            (0, 2) => new(maximum.X, maximum.Y, maximum.Z),
            (0, _) => new(minimum.X, maximum.Y, maximum.Z),
            (1, 0) => new(minimum.X, minimum.Y, maximum.Z),
            (1, 1) => new(minimum.X, minimum.Y, minimum.Z),
            (1, 2) => new(minimum.X, maximum.Y, minimum.Z),
            (1, _) => new(minimum.X, maximum.Y, maximum.Z),
            (2, 0) => new(maximum.X, minimum.Y, minimum.Z),
            (2, 1) => new(maximum.X, minimum.Y, maximum.Z),
            (2, 2) => new(maximum.X, maximum.Y, maximum.Z),
            (2, _) => new(maximum.X, maximum.Y, minimum.Z),
            (3, 0) => new(minimum.X, minimum.Y, minimum.Z),
            (3, 1) => new(maximum.X, minimum.Y, minimum.Z),
            (3, 2) => new(maximum.X, maximum.Y, minimum.Z),
            (3, _) => new(minimum.X, maximum.Y, minimum.Z),
            (4, 0) => new(maximum.X, minimum.Y, maximum.Z),
            (4, 1) => new(minimum.X, minimum.Y, maximum.Z),
            (4, 2) => new(minimum.X, maximum.Y, maximum.Z),
            (4, _) => new(maximum.X, maximum.Y, maximum.Z),
            (5, 0) => new(minimum.X, minimum.Y, maximum.Z),
            (5, 1) => new(maximum.X, minimum.Y, maximum.Z),
            (5, 2) => new(maximum.X, minimum.Y, minimum.Z),
            _ => new(minimum.X, minimum.Y, minimum.Z),
        };

    private static void Add(List<MechanicalPartBox> parts, MechanicalAxis axis, Vector3 minimum, Vector3 maximum)
    {
        Vector3 Transform(Vector3 value) => axis switch
        {
            MechanicalAxis.Y => new(value.Y, value.X, value.Z),
            MechanicalAxis.Z => new(value.Y, value.Z, value.X),
            _ => value,
        };
        Vector3 a = Transform(minimum);
        Vector3 b = Transform(maximum);
        parts.Add(new MechanicalPartBox(Vector3.Min(a, b), Vector3.Max(a, b)));
    }

    private static bool HasTag(BlockDefinition definition, string tag) =>
        definition.Tags.Any(value => value.Equals(tag, StringComparison.OrdinalIgnoreCase));

    private static bool IsMechanicalId(ushort id, IReadOnlyList<BlockDefinition> definitions) =>
        id != 0 && id < definitions.Count && IsMechanical(definitions[id]);
}
