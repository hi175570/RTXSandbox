using System.Numerics;

namespace RTXSandbox.World;

/// <summary>
/// A rigid, mathematically spherical world object. Renderers must intersect the
/// implicit equation |p - center|^2 = radius^2; this is not a tessellated mesh.
/// </summary>
public sealed class AnalyticSphere
{
    internal AnalyticSphere(ulong id, Vector3 center, float radius, ushort materialId)
    {
        Id = id;
        Center = center;
        Radius = Math.Clamp(radius, 0.2f, 4f);
        MaterialId = materialId;
    }

    public ulong Id { get; }
    public Vector3 Center { get; internal set; }
    public float Radius { get; }
    public ushort MaterialId { get; }
    public Vector3 LinearVelocity { get; internal set; }
    public Vector3 AngularVelocity { get; internal set; }
    public bool IsSleeping { get; internal set; }
}

public readonly record struct AnalyticSphereHit(
    ulong SphereId,
    Vector3 Position,
    Vector3 Normal,
    float Distance,
    ushort MaterialId);

public sealed class AnalyticSphereWorld
{
    private readonly List<AnalyticSphere> _spheres = new();
    private ulong _nextId = 1;

    public IReadOnlyList<AnalyticSphere> Spheres => _spheres;
    public int Count => _spheres.Count;
    public event Action? Changed;

    public AnalyticSphere Create(Vector3 center, float radius, ushort materialId)
    {
        if (!float.IsFinite(center.X) || !float.IsFinite(center.Y) || !float.IsFinite(center.Z))
            throw new ArgumentOutOfRangeException(nameof(center));
        if (!float.IsFinite(radius)) throw new ArgumentOutOfRangeException(nameof(radius));
        if (materialId == 0) throw new ArgumentOutOfRangeException(nameof(materialId));

        var sphere = new AnalyticSphere(_nextId++, center, radius, materialId);
        _spheres.Add(sphere);
        Changed?.Invoke();
        return sphere;
    }

    public bool Remove(ulong id)
    {
        int index = _spheres.FindIndex(sphere => sphere.Id == id);
        if (index < 0) return false;
        _spheres.RemoveAt(index);
        Changed?.Invoke();
        return true;
    }

    public AnalyticSphere? Get(ulong id) => _spheres.FirstOrDefault(sphere => sphere.Id == id);

    public AnalyticSphereHit? Raycast(Vector3 origin, Vector3 direction, float maxDistance)
    {
        if (!float.IsFinite(maxDistance) || maxDistance <= 0f || direction.LengthSquared() < 0.000001f)
            return null;

        direction = Vector3.Normalize(direction);
        AnalyticSphereHit? nearest = null;
        foreach (AnalyticSphere sphere in _spheres)
        {
            Vector3 offset = origin - sphere.Center;
            float projected = Vector3.Dot(offset, direction);
            float discriminant = projected * projected -
                                 (Vector3.Dot(offset, offset) - sphere.Radius * sphere.Radius);
            if (discriminant < 0f) continue;

            float root = MathF.Sqrt(discriminant);
            float distance = -projected - root;
            if (distance <= 0.0001f) distance = -projected + root;
            if (distance <= 0.0001f || distance > maxDistance ||
                nearest.HasValue && distance >= nearest.Value.Distance)
                continue;

            Vector3 position = origin + direction * distance;
            nearest = new AnalyticSphereHit(
                sphere.Id,
                position,
                Vector3.Normalize(position - sphere.Center),
                distance,
                sphere.MaterialId);
        }
        return nearest;
    }

    public void Clear()
    {
        if (_spheres.Count == 0) return;
        _spheres.Clear();
        Changed?.Invoke();
    }

    internal void NotifyChanged() => Changed?.Invoke();
}
