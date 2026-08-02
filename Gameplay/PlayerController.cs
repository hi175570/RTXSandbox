using System.Numerics;
using RTXSandbox.World;

namespace RTXSandbox.Gameplay;

public sealed class PlayerController
{
    public const float Radius = 0.32f;
    public const float Height = 1.8f;
    public const float EyeHeight = 1.62f;

    private readonly VoxelWorld _world;
    private readonly List<VoxelAabb> _collisionBoxes = new(24);
    private float _verticalVelocity;

    public Vector3 Position { get; private set; }
    public bool IsGrounded { get; private set; }
    public bool IsInWater { get; private set; }
    public bool IsHeadUnderwater { get; private set; }
    public bool LandedThisFrame { get; private set; }
    public float LastLandingSpeed { get; private set; }
    public float VerticalVelocity => _verticalVelocity;
    public bool FlightMode { get; set; }
    public bool NoClip { get; set; }

    public Vector3 EyePosition => Position + Vector3.UnitY * EyeHeight;

    public PlayerController(VoxelWorld world, Vector3 spawnPosition)
    {
        _world = world;
        Position = spawnPosition;
    }

    public void Update(
        float deltaTime,
        Vector2 moveInput,
        Vector3 forward,
        bool sprint,
        bool jump,
        bool descend = false)
    {
        deltaTime = Math.Clamp(deltaTime, 0f, 0.05f);
        LandedThisFrame = false;
        LastLandingSpeed = 0f;
        Vector3 flatForward = new(forward.X, 0f, forward.Z);
        if (flatForward.LengthSquared() < 0.001f)
        {
            flatForward = -Vector3.UnitZ;
        }
        flatForward = Vector3.Normalize(flatForward);
        Vector3 right = Vector3.Normalize(Vector3.Cross(flatForward, Vector3.UnitY));
        Vector3 desired = flatForward * moveInput.Y + right * moveInput.X;
        if (desired.LengthSquared() > 1f)
        {
            desired = Vector3.Normalize(desired);
        }

        if (!FlightMode && !NoClip && _world.PhysicsGrids.TryGetPlatformVelocity(Position, out Vector3 platformVelocity))
        {
            Vector3 platformDisplacement = platformVelocity * deltaTime;
            MoveAxis(0, platformDisplacement.X);
            MoveAxis(1, platformDisplacement.Y);
            MoveAxis(2, platformDisplacement.Z);
        }

        if (FlightMode || NoClip)
        {
            Vector3 flyForward = Vector3.Normalize(forward);
            Vector3 flyRight = Vector3.Cross(flyForward, Vector3.UnitY);
            if (flyRight.LengthSquared() < 0.001f) flyRight = Vector3.UnitX;
            else flyRight = Vector3.Normalize(flyRight);
            float verticalInput = (jump ? 1f : 0f) - (descend ? 1f : 0f);
            Vector3 flyMotion = flyForward * moveInput.Y + flyRight * moveInput.X + Vector3.UnitY * verticalInput;
            if (flyMotion.LengthSquared() > 1f) flyMotion = Vector3.Normalize(flyMotion);
            float flySpeed = sprint ? 18f : 8f;
            Vector3 displacement = flyMotion * flySpeed * deltaTime;
            if (NoClip)
            {
                Position += displacement;
            }
            else
            {
                MoveAxis(0, displacement.X);
                MoveAxis(1, displacement.Y);
                MoveAxis(2, displacement.Z);
            }
            _verticalVelocity = 0f;
            IsGrounded = false;
            IsInWater = false;
            IsHeadUnderwater = false;
            return;
        }

        IsInWater = IsFluidAt(Position + Vector3.UnitY * 0.25f) ||
                    IsFluidAt(Position + Vector3.UnitY * 1.1f);
        IsHeadUnderwater = IsFluidAt(EyePosition);
        float speed = IsInWater ? 3.4f : sprint ? 8.5f : 5.2f;
        MoveAxis(0, desired.X * speed * deltaTime);
        MoveAxis(2, desired.Z * speed * deltaTime);

        if (IsInWater)
        {
            float targetVerticalSpeed = jump ? 3.6f : descend ? -3.0f : 0.45f;
            float response = 1f - MathF.Exp(-5.5f * deltaTime);
            _verticalVelocity += (targetVerticalSpeed - _verticalVelocity) * response;
            IsGrounded = false;
            MoveAxis(1, _verticalVelocity * deltaTime);
            return;
        }

        if (jump && IsGrounded)
        {
            _verticalVelocity = 7.2f;
            IsGrounded = false;
        }

        _verticalVelocity -= 20f * deltaTime;
        IsGrounded = false;
        MoveAxis(1, _verticalVelocity * deltaTime);
    }

    public bool WouldIntersectBlock(int blockX, int blockY, int blockZ)
        => WouldIntersectAabb(new Vector3(blockX, blockY, blockZ), new Vector3(blockX + 1f, blockY + 1f, blockZ + 1f));

    public bool WouldIntersectAabb(Vector3 boxMinimum, Vector3 boxMaximum)
    {
        Vector3 min = BoundsMin(Position);
        Vector3 max = BoundsMax(Position);
        return min.X < boxMaximum.X && max.X > boxMinimum.X &&
               min.Y < boxMaximum.Y && max.Y > boxMinimum.Y &&
               min.Z < boxMaximum.Z && max.Z > boxMinimum.Z;
    }

    public void Teleport(Vector3 position)
    {
        Position = position;
        _verticalVelocity = 0f;
        IsGrounded = false;
        IsInWater = false;
        IsHeadUnderwater = false;
        LandedThisFrame = false;
        LastLandingSpeed = 0f;
    }

    private void MoveAxis(int axis, float amount)
    {
        if (MathF.Abs(amount) < 0.000001f)
        {
            return;
        }

        Vector3 candidate = Position;
        candidate = axis switch
        {
            0 => candidate with { X = candidate.X + amount },
            1 => candidate with { Y = candidate.Y + amount },
            _ => candidate with { Z = candidate.Z + amount },
        };

        Vector3 minimum = BoundsMin(candidate);
        Vector3 maximum = BoundsMax(candidate);
        const float epsilon = 0.0001f;
        int minX = (int)MathF.Floor(minimum.X);
        int minY = (int)MathF.Floor(minimum.Y);
        int minZ = (int)MathF.Floor(minimum.Z);
        int maxX = (int)MathF.Floor(maximum.X - epsilon);
        int maxY = (int)MathF.Floor(maximum.Y - epsilon);
        int maxZ = (int)MathF.Floor(maximum.Z - epsilon);

        bool collided = false;
        for (int z = minZ; z <= maxZ; z++)
        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            _collisionBoxes.Clear();
            _world.GetCollisionBoxes(x, y, z, _collisionBoxes);
            foreach (VoxelAabb box in _collisionBoxes)
            {
                if (!box.Intersects(minimum, maximum)) continue;

                collided = true;
                if (axis == 0)
                {
                    candidate.X = amount > 0f
                        ? MathF.Min(candidate.X, box.Minimum.X - Radius)
                        : MathF.Max(candidate.X, box.Maximum.X + Radius);
                }
                else if (axis == 1)
                {
                    if (amount > 0f)
                    {
                        candidate.Y = MathF.Min(candidate.Y, box.Minimum.Y - Height);
                    }
                    else
                    {
                        LastLandingSpeed = MathF.Max(LastLandingSpeed, -_verticalVelocity);
                        LandedThisFrame = LastLandingSpeed > 0f;
                        candidate.Y = MathF.Max(candidate.Y, box.Maximum.Y);
                        IsGrounded = true;
                    }
                    _verticalVelocity = 0f;
                }
                else
                {
                    candidate.Z = amount > 0f
                        ? MathF.Min(candidate.Z, box.Minimum.Z - Radius)
                        : MathF.Max(candidate.Z, box.Maximum.Z + Radius);
                }
            }
        }

        Position = candidate;

        // Collision iteration above is deterministic for voxel AABBs; this guard catches
        // accidental tunnelling if movement tuning changes later.
        if (collided && _world.IntersectsSolid(BoundsMin(Position), BoundsMax(Position)))
        {
            Position = axis switch
            {
                0 => Position with { X = Position.X - amount },
                1 => Position with { Y = Position.Y - amount },
                _ => Position with { Z = Position.Z - amount },
            };
        }
    }

    private static Vector3 BoundsMin(Vector3 position) =>
        new(position.X - Radius, position.Y, position.Z - Radius);

    private static Vector3 BoundsMax(Vector3 position) =>
        new(position.X + Radius, position.Y + Height, position.Z + Radius);

    private bool IsFluidAt(Vector3 point) => _world.IsFluid(
        (int)MathF.Floor(point.X),
        (int)MathF.Floor(point.Y),
        (int)MathF.Floor(point.Z));
}
