using System.Numerics;

namespace RTXSandbox.Infrastructure;

/// <summary>
/// Converts Unity's left-handed world into the right-handed world used by the RTX renderers.
/// The conversion is one reflection through the XY plane. Positions, camera axes and object
/// rotations must all pass through the same reflection or an overlay appears to rotate in the
/// opposite direction over the Unity scene.
/// </summary>
internal static class ManifoldGardenCoordinates
{
    public static Vector3 Position(Vector3 unity) => new(unity.X, unity.Y, -unity.Z);

    public static Quaternion Rotation(Quaternion unity) => Quaternion.Normalize(
        new Quaternion(-unity.X, -unity.Y, unity.Z, unity.W));

    public static Vector3 CameraForward(Quaternion unity) =>
        Vector3.Transform(-Vector3.UnitZ, Rotation(unity));

    public static Vector3 CameraUp(Quaternion unity) =>
        Vector3.Transform(Vector3.UnitY, Rotation(unity));
}
