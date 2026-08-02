using System.Numerics;

namespace RTXSandbox.Gameplay;

public sealed class Camera
{
    private float _yaw = -90f;
    private float _pitch = -12f;

    public float FieldOfViewDegrees { get; set; } = 72f;
    public Vector3 Forward { get; private set; } = -Vector3.UnitZ;

    public Camera()
    {
        RecalculateForward();
    }

    public void Rotate(Vector2 mouseDelta, float sensitivity = 0.11f)
    {
        _yaw += mouseDelta.X * sensitivity;
        _pitch = Math.Clamp(_pitch - mouseDelta.Y * sensitivity, -89f, 89f);
        RecalculateForward();
    }

    private void RecalculateForward()
    {
        float yaw = MathF.PI / 180f * _yaw;
        float pitch = MathF.PI / 180f * _pitch;
        Forward = Vector3.Normalize(new Vector3(
            MathF.Cos(pitch) * MathF.Cos(yaw),
            MathF.Sin(pitch),
            MathF.Cos(pitch) * MathF.Sin(yaw)));
    }
}
