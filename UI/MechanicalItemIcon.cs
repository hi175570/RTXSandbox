using System.Numerics;
using ImGuiNET;
using RTXSandbox.Engine;
using RTXSandbox.Rendering;

namespace RTXSandbox.UI;

/// <summary>
/// Inventory preview rasterized from the exact procedural world mesh. It is not a
/// hand-drawn symbol: changing a shaft, hub, spoke or tooth profile changes both the
/// world object and its item preview.
/// </summary>
public static class MechanicalItemIcon
{
    private static readonly Matrix4x4 PreviewRotation =
        Matrix4x4.CreateRotationZ(-0.12f) *
        Matrix4x4.CreateRotationY(0.58f) *
        Matrix4x4.CreateRotationX(-0.22f);

    public static void Draw(ImDrawListPtr drawList, Vector2 position, Vector2 size, BlockDefinition block)
    {
        IReadOnlyList<MechanicalPartMeshVertex> mesh =
            MechanicalPartGeometry.BuildVisualMesh(block, MechanicalAxis.X);
        if (mesh.Count < 3) return;

        Vector2 center = position + size * 0.5f;
        float scale = MathF.Min(size.X, size.Y) * 0.88f;
        Vector3 baseColor = Vector3.Clamp(block.Albedo, new Vector3(0.055f), Vector3.One);
        Vector3 lightDirection = Vector3.Normalize(new Vector3(-0.45f, 0.78f, 0.62f));
        var triangles = new List<PreviewTriangle>(mesh.Count / 3);

        for (int index = 0; index + 2 < mesh.Count; index += 3)
        {
            MechanicalPartMeshVertex va = mesh[index];
            MechanicalPartMeshVertex vb = mesh[index + 1];
            MechanicalPartMeshVertex vc = mesh[index + 2];
            Vector3 a = Vector3.Transform(va.Position - new Vector3(0.5f), PreviewRotation);
            Vector3 b = Vector3.Transform(vb.Position - new Vector3(0.5f), PreviewRotation);
            Vector3 c = Vector3.Transform(vc.Position - new Vector3(0.5f), PreviewRotation);
            Vector3 normal = Vector3.Normalize(Vector3.TransformNormal(va.Normal, PreviewRotation));
            float diffuse = 0.34f + MathF.Max(0f, Vector3.Dot(normal, lightDirection)) * 0.58f;
            float rim = MathF.Pow(1f - MathF.Abs(normal.X), 3f) * (0.08f + block.Metallic * 0.10f);
            Vector3 color = Vector3.Clamp(baseColor * diffuse + new Vector3(rim), Vector3.Zero, Vector3.One);
            triangles.Add(new PreviewTriangle(
                Project(a, center, scale), Project(b, center, scale), Project(c, center, scale),
                (a.X + b.X + c.X) / 3f,
                ImGui.ColorConvertFloat4ToU32(new Vector4(color, 1f))));
        }

        foreach (PreviewTriangle triangle in triangles.OrderBy(value => value.Depth))
            drawList.AddTriangleFilled(triangle.A, triangle.B, triangle.C, triangle.Color);
    }

    private static Vector2 Project(Vector3 point, Vector2 center, float scale) =>
        center + new Vector2(point.Z, -point.Y) * scale;

    private readonly record struct PreviewTriangle(
        Vector2 A,
        Vector2 B,
        Vector2 C,
        float Depth,
        uint Color);
}
