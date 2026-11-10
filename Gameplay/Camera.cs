using System.Numerics;

namespace RTXSandbox.Gameplay;

public sealed class Camera
{
    private float _yaw = -90f;
    private float _pitch = -12f;

    public float FieldOfViewDegrees { get; set; } = 72f;
    public Vector3 Forward { get; private set; } = -Vector3.UnitZ;
    public Vector3 Up { get; private set; } = Vector3.UnitY;

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

    public void LookAt(Vector3 direction)
    {
        if (direction.LengthSquared() < 0.000001f) return;
        direction = Vector3.Normalize(direction);
        _pitch = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f)) * 180f / MathF.PI;
        _yaw = MathF.Atan2(direction.Z, direction.X) * 180f / MathF.PI;
        RecalculateForward();
    }

    public void SetPose(Vector3 forward, Vector3 up)
    {
        if (forward.LengthSquared() < 0.000001f) return;
        forward = Vector3.Normalize(forward);
        up -= forward * Vector3.Dot(up, forward);
        if (up.LengthSquared() < 0.000001f)
        {
            up = MathF.Abs(Vector3.Dot(forward, Vector3.UnitY)) < 0.98f
                ? Vector3.UnitY
                : Vector3.UnitZ;
            up -= forward * Vector3.Dot(up, forward);
        }
        Forward = forward;
        Up = Vector3.Normalize(up);
        _pitch = MathF.Asin(Math.Clamp(forward.Y, -1f, 1f)) * 180f / MathF.PI;
        _yaw = MathF.Atan2(forward.Z, forward.X) * 180f / MathF.PI;
    }

    private void RecalculateForward()
    {
        float yaw = MathF.PI / 180f * _yaw;
        float pitch = MathF.PI / 180f * _pitch;
        Forward = Vector3.Normalize(new Vector3(
            MathF.Cos(pitch) * MathF.Cos(yaw),
            MathF.Sin(pitch),
            MathF.Cos(pitch) * MathF.Sin(yaw)));
        Up = Vector3.UnitY;
    }
}
