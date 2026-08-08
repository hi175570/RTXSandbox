using System.Numerics;
using RTXSandbox.World;

namespace RTXSandbox.Gameplay;

public sealed class PlayerController
{
    public const float Radius = 0.32f;
    public const float Height = 1.8f;
    public const float EyeHeight = 1.62f;
    public const float CrouchedHeight = 1.5f;
    public const float CrouchedEyeHeight = 1.27f;
    private const float ExternalDepenetrationSpeed = 2.4f;
    private const float WaterJumpSpeed = 6.2f;

    private readonly VoxelWorld _world;
    private readonly List<VoxelAabb> _collisionBoxes = new(24);
    private float _verticalVelocity;
    private Vector3 _externalPushDirection;

    public Vector3 Position { get; private set; }
    public bool IsGrounded { get; private set; }
    public bool IsInWater { get; private set; }
    public bool IsHeadUnderwater { get; private set; }
    public bool LandedThisFrame { get; private set; }
    public float LastLandingSpeed { get; private set; }
    public float VerticalVelocity => _verticalVelocity;
    public bool FlightMode { get; set; }
    public bool NoClip { get; set; }
    public bool IsCrouching { get; private set; }
    public float LastExternalPushDistance { get; private set; }

    public Vector3 EyePosition => Position + Vector3.UnitY *
        (IsCrouching ? CrouchedEyeHeight : EyeHeight);

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
        bool descend = false,
        bool crouch = false)
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
            IsCrouching = false;
            // Flight translation uses stable world axes. Looking up, down or to
            // either side must not rotate W/A/S/D movement.
            Vector3 flyForward = -Vector3.UnitZ;
            Vector3 flyRight = Vector3.UnitX;
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

        // The lowest finite-water layer is only 1/16 of a full cell. Sample close
        // to the feet so even that layer can provide the same deliberate jump-out
        // action as deep water; visual depth must not weaken the control response.
        IsInWater = IsFluidAt(Position + Vector3.UnitY * 0.02f) ||
                    IsFluidAt(Position + Vector3.UnitY * 1.1f);
        IsHeadUnderwater = IsFluidAt(EyePosition);
        if (IsInWater)
        {
            IsCrouching = false;
        }
        else if (crouch)
        {
            IsCrouching = true;
        }
        else if (IsCrouching && CanStand())
        {
            IsCrouching = false;
        }

        float speed = IsInWater ? 3.4f : IsCrouching ? 2.1f : sprint ? 8.5f : 5.2f;
        bool protectEdge = IsCrouching && IsGrounded && !jump;
        bool blockedX = MoveHorizontalAxis(0, desired.X * speed * deltaTime, protectEdge);
        bool blockedZ = MoveHorizontalAxis(2, desired.Z * speed * deltaTime, protectEdge);

        // Shore assistance is input-driven: colliding with a wall must never raise
        // the player unless they are actively holding the jump/swim-up control.
        if (IsInWater && jump && (blockedX || blockedZ) && desired.LengthSquared() > 0.001f)
        {
            float climb = MathF.Min(6f * deltaTime, 0.16f);
            float beforeClimb = Position.Y;
            MoveAxis(1, climb);
            if (Position.Y > beforeClimb + 0.0001f)
            {
                if (blockedX) MoveAxis(0, desired.X * speed * deltaTime);
                if (blockedZ) MoveAxis(2, desired.Z * speed * deltaTime);
                _verticalVelocity = MathF.Max(_verticalVelocity, 4.8f);
            }
        }

        if (IsInWater)
        {
            if (jump)
            {
                // Jumping is an explicit escape action, not buoyancy. Apply the
                // complete impulse immediately so a thin layer and a deep column
                // launch the player with exactly the same upward speed.
                _verticalVelocity = MathF.Max(_verticalVelocity, WaterJumpSpeed);
            }
            else
            {
                float targetVerticalSpeed = descend ? -3.2f : 0f;
                float response = 1f - MathF.Exp(-8f * deltaTime);
                _verticalVelocity += (targetVerticalSpeed - _verticalVelocity) * response;
            }
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

    public bool ResolveExternalPenetration(float deltaTime)
    {
        LastExternalPushDistance = 0f;
        if (NoClip) return false;
        if (!_world.PhysicsGrids.TryCalculatePlayerPenetration(
                BoundsMin(Position), BoundsMax(Position), out Vector3 correction, out _))
            return false;

        float correctionLength = correction.Length();
        if (correctionLength < 0.000001f) return false;
        Vector3 direction = FindHorizontalEscapeDirection();
        if (direction.LengthSquared() < 0.5f)
        {
            // Vertical correction is a last resort only. It remains rate-limited, so a
            // completely boxed-in player can still escape without being teleported.
            Vector3 fallback = correction / correctionLength;
            direction = fallback.Y > 0.1f && IsStaticSafe(Position - Vector3.UnitY * 0.1f)
                ? -Vector3.UnitY
                : fallback;
        }
        _externalPushDirection = direction;
        // This is an anti-stuck recovery, not collision impulse transfer. A fast Grid
        // must never launch the player faster than a stationary overlapping Grid.
        float maximumDistance = ExternalDepenetrationSpeed * Math.Clamp(deltaTime, 0f, 0.05f);
        Vector3 boundedCorrection = direction * maximumDistance;
        Vector3 before = Position;
        MoveAxis(0, boundedCorrection.X, includeDynamicGrids: false);
        MoveAxis(2, boundedCorrection.Z, includeDynamicGrids: false);
        if (MathF.Abs(boundedCorrection.Y) > 0.0001f)
            MoveAxis(1, boundedCorrection.Y, includeDynamicGrids: false);
        LastExternalPushDistance = Vector3.Distance(before, Position);
        return LastExternalPushDistance > 0.000001f;
    }

    private Vector3 FindHorizontalEscapeDirection()
    {
        Vector3[] directions =
        {
            Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ,
            Vector3.Normalize(Vector3.UnitX + Vector3.UnitZ),
            Vector3.Normalize(Vector3.UnitX - Vector3.UnitZ),
            Vector3.Normalize(-Vector3.UnitX + Vector3.UnitZ),
            Vector3.Normalize(-Vector3.UnitX - Vector3.UnitZ),
        };

        Vector3 best = Vector3.Zero;
        float bestDistance = float.MaxValue;
        foreach (Vector3 direction in directions)
        {
            float preference = _externalPushDirection.LengthSquared() > 0.5f &&
                               Vector3.Dot(direction, _externalPushDirection) > 0.98f ? -0.04f : 0f;
            for (float distance = 0.1f; distance <= 2.4f; distance += 0.1f)
            {
                Vector3 candidate = Position + direction * distance;
                if (!IsStaticSafe(candidate)) break;
                if (_world.PhysicsGrids.TryCalculatePlayerPenetration(
                        BoundsMin(candidate), BoundsMax(candidate), out _, out _)) continue;
                if (distance + preference < bestDistance)
                {
                    bestDistance = distance + preference;
                    best = direction;
                }
                break;
            }
        }
        return best;
    }

    private bool IsStaticSafe(Vector3 position) =>
        !_world.IntersectsStaticSolid(BoundsMin(position), BoundsMax(position));

    private bool CanStand()
    {
        Vector3 maximum = new(Position.X + Radius, Position.Y + Height, Position.Z + Radius);
        return !_world.IntersectsSolid(BoundsMin(Position), maximum);
    }

    private bool MoveHorizontalAxis(int axis, float amount, bool protectEdge)
    {
        Vector3 before = Position;
        bool blocked = MoveAxis(axis, amount);
        if (!protectEdge || HasFootSupport(Position)) return blocked;

        Position = before;
        return true;
    }

    private bool HasFootSupport(Vector3 position)
    {
        var minimum = new Vector3(position.X - Radius, position.Y - 0.08f, position.Z - Radius);
        var maximum = new Vector3(position.X + Radius, position.Y - 0.001f, position.Z + Radius);
        return _world.IntersectsSolid(minimum, maximum);
    }

    private bool MoveAxis(int axis, float amount, bool includeDynamicGrids = true)
    {
        if (MathF.Abs(amount) < 0.000001f)
        {
            return false;
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
            if (includeDynamicGrids) _world.GetCollisionBoxes(x, y, z, _collisionBoxes);
            else _world.GetStaticCollisionBoxes(x, y, z, _collisionBoxes);
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
                        candidate.Y = MathF.Min(candidate.Y, box.Minimum.Y - CurrentHeight);
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
        bool stillIntersecting = includeDynamicGrids
            ? _world.IntersectsSolid(BoundsMin(Position), BoundsMax(Position))
            : _world.IntersectsStaticSolid(BoundsMin(Position), BoundsMax(Position));
        if (collided && stillIntersecting)
        {
            Position = axis switch
            {
                0 => Position with { X = Position.X - amount },
                1 => Position with { Y = Position.Y - amount },
                _ => Position with { Z = Position.Z - amount },
            };
        }
        return collided;
    }

    private static Vector3 BoundsMin(Vector3 position) =>
        new(position.X - Radius, position.Y, position.Z - Radius);

    private float CurrentHeight => IsCrouching ? CrouchedHeight : Height;

    private Vector3 BoundsMax(Vector3 position) =>
        new(position.X + Radius, position.Y + CurrentHeight, position.Z + Radius);

    private bool IsFluidAt(Vector3 point)
    {
        int x = (int)MathF.Floor(point.X);
        int y = (int)MathF.Floor(point.Y);
        int z = (int)MathF.Floor(point.Z);
        return _world.IsFluid(x, y, z) &&
               (point.Y - y < VoxelWorld.GetFluidSurfaceHeight(_world.GetFluidLevel(x, y, z)) ||
                _world.IsFluid(x, y + 1, z));
    }
}
