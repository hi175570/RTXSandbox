using System.Numerics;
using System.Globalization;
using RTXSandbox.Engine;
using RTXSandbox.World;

namespace RTXSandbox.Gameplay;

/// <summary>Shared direct-placement rules for analytic sphere inventory items.</summary>
public static class AnalyticSpherePlacement
{
    private const float SurfaceGap = 0.025f;
    private const float FreePlacementDistance = 3f;
    private const float DefaultRadius = 0.65f;

    public static bool IsSphereItem(BlockDefinition definition) =>
        definition.Tags.Any(tag => tag.Equals("analytic-sphere", StringComparison.OrdinalIgnoreCase));

    public static bool IsBlackHole(BlockDefinition definition) =>
        definition.Tags.Any(tag => tag.Equals("black-hole", StringComparison.OrdinalIgnoreCase));

    public static float Radius(BlockDefinition definition)
    {
        const string prefix = "sphere-radius:";
        string? tag = definition.Tags.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return tag is not null &&
               float.TryParse(tag[prefix.Length..], NumberStyles.Float, CultureInfo.InvariantCulture, out float value) &&
               float.IsFinite(value)
            ? Math.Clamp(value, 0.2f, 4f)
            : DefaultRadius;
    }

    public static bool TryResolveCenter(
        Vector3 eye,
        Vector3 forward,
        float radius,
        VoxelHit? voxelHit,
        AnalyticSphereHit? sphereHit,
        out Vector3 center)
    {
        center = default;
        if (!float.IsFinite(eye.X) || !float.IsFinite(eye.Y) || !float.IsFinite(eye.Z) ||
            forward.LengthSquared() < 0.000001f || !float.IsFinite(radius))
            return false;

        forward = Vector3.Normalize(forward);
        radius = Math.Clamp(radius, 0.2f, 4f);
        if (sphereHit.HasValue)
        {
            AnalyticSphereHit hit = sphereHit.Value;
            if (!PlacementRules.IsFarEnough(hit.Distance)) return false;
            center = hit.Position + hit.Normal * (radius + SurfaceGap);
            return IsFinite(center);
        }

        if (voxelHit.HasValue)
        {
            VoxelHit hit = voxelHit.Value;
            if (!PlacementRules.IsFarEnough(hit.Distance)) return false;
            Vector3 position = eye + forward * hit.Distance;
            center = position + hit.Normal * (radius + SurfaceGap);
            return IsFinite(center);
        }

        center = eye + forward * MathF.Max(FreePlacementDistance, radius + PlacementRules.MinimumEyeDistance);
        return IsFinite(center);
    }

    public static AnalyticSphere? TryPlace(
        AnalyticSphereWorld world,
        BlockDefinition definition,
        Vector3 eye,
        Vector3 forward,
        VoxelHit? voxelHit,
        AnalyticSphereHit? sphereHit)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(definition);
        if (!IsSphereItem(definition) || definition.Id == 0 ||
            !TryResolveCenter(eye, forward, Radius(definition), voxelHit, sphereHit, out Vector3 center))
            return null;

        AnalyticSphere sphere = world.Create(center, Radius(definition), definition.Id);
        if (IsBlackHole(definition))
        {
            // A placed black hole is a world-space optical object, not a BEPU ball.
            // Keeping it kinematic also prevents player contact or projectiles from
            // moving the lens independently of its rendered event horizon.
            sphere.IsKinematic = true;
            sphere.IsSleeping = true;
        }
        return sphere;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
