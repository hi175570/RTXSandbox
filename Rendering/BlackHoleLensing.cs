using System.Numerics;
using OpenTK.Graphics.OpenGL4;
using RTXSandbox.Engine;
using RTXSandbox.Gameplay;
using RTXSandbox.World;

namespace RTXSandbox.Rendering;

/// <summary>Projects analytic black holes into the shared ray-renderer display pass.</summary>
internal static class BlackHoleLensing
{
    private const int MaximumVisibleBlackHoles = 4;
    private static bool _smokeProjectionReported;

    public static void Upload(
        ShaderProgram shader,
        VoxelWorld world,
        BlockRegistry blocks,
        Vector3 cameraPosition,
        Camera camera,
        int width,
        int height)
    {
        Vector3 forward = Vector3.Normalize(camera.Forward);
        Vector3 right = Vector3.Cross(forward, Vector3.UnitY);
        if (right.LengthSquared() < 0.000001f) right = Vector3.UnitX;
        else right = Vector3.Normalize(right);
        Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));
        float tanHalfFov = MathF.Tan(camera.FieldOfViewDegrees * MathF.PI / 360f);
        float aspect = Math.Max(width, 1) / (float)Math.Max(height, 1);
        var projected = new List<(float Distance, Vector4 Data)>();

        foreach (AnalyticSphere sphere in world.AnalyticSpheres.Spheres)
        {
            BlockDefinition material = blocks.Get(sphere.MaterialId);
            if (!AnalyticSpherePlacement.IsBlackHole(material)) continue;
            Vector3 offset = sphere.Center - cameraPosition;
            float depth = Vector3.Dot(offset, forward);
            if (depth <= sphere.Radius * 0.25f) continue;
            float ndcX = Vector3.Dot(offset, right) / (depth * tanHalfFov * aspect);
            float ndcY = Vector3.Dot(offset, up) / (depth * tanHalfFov);
            float uvRadius = sphere.Radius / (depth * tanHalfFov) * 0.5f;
            if (uvRadius <= 0.00001f || MathF.Abs(ndcX) > 1f + uvRadius * 8.4f ||
                MathF.Abs(ndcY) > 1f + uvRadius * 8.4f)
                continue;
            projected.Add((depth, new Vector4(ndcX * 0.5f + 0.5f, ndcY * 0.5f + 0.5f,
                uvRadius, 1f)));
        }

        Vector4[] holes = projected
            .OrderBy(entry => entry.Distance)
            .Take(MaximumVisibleBlackHoles)
            .Select(entry => entry.Data)
            .ToArray();
        if (!_smokeProjectionReported &&
            Environment.GetEnvironmentVariable("RTX_SANDBOX_GPU_SMOKE_BLACK_HOLE") == "1")
        {
            string values = string.Join(", ", holes.Select(hole =>
                $"uv=({hole.X:0.000},{hole.Y:0.000}) radius={hole.Z:0.000}"));
            Console.WriteLine($"Black-hole lens projection: {holes.Length} visible; {values}.");
            _smokeProjectionReported = true;
        }
        GL.Uniform1(shader.Uniform("uBlackHoleCount"), holes.Length);
        for (int index = 0; index < MaximumVisibleBlackHoles; index++)
        {
            Vector4 value = index < holes.Length ? holes[index] : Vector4.Zero;
            GL.Uniform4(shader.Uniform($"uBlackHoles[{index}]"), value.X, value.Y, value.Z, value.W);
        }
    }
}
