using System.Globalization;
using System.Numerics;
using RTXSandbox.Engine;
using RTXSandbox.World;

namespace RTXSandbox.Gameplay;

/// <summary>
/// Simulates fixed machine frames and transfers output motion to separate rigid bodies.
/// A motor housing never animates the complete PhysicsGrid: the assembly must touch
/// static world geometry to run, and losing that support immediately returns it to gravity.
/// </summary>
public sealed class KinematicStructureSystem
{
    private const float TwoPi = MathF.PI * 2f;
    private readonly VoxelWorld _world;
    private readonly BlockRegistry _blocks;
    private readonly Dictionary<ulong, Controller> _controllers = new();

    public int ActiveControllerCount => _controllers.Count;

    public KinematicStructureSystem(VoxelWorld world, BlockRegistry blocks)
    {
        _world = world;
        _blocks = blocks;
    }

    public void Update(float deltaSeconds)
    {
        float dt = Math.Clamp(deltaSeconds, 0f, 0.05f);
        if (dt <= 0f) return;
        Discover();
        bool changed = false;
        foreach (Controller controller in _controllers.Values.ToArray())
        {
            if (!HasStaticAnchor(controller.Grid))
            {
                Release(controller);
                continue;
            }
            AdvancePowertrain(controller, dt);
            changed |= Drive(controller, dt);
        }
        if (changed) _world.PhysicsGrids.NotifyChanged();
    }

    public bool TryGetTelemetry(ulong gridId, out MechanicalTelemetry telemetry)
    {
        if (_controllers.TryGetValue(gridId, out Controller? controller))
        {
            telemetry = new MechanicalTelemetry(controller.Powered, controller.ShaftSpeed * 60f / TwoPi,
                controller.OutputTorque, controller.LastLoad, controller.GearRatio, controller.Stalled);
            return true;
        }
        telemetry = default;
        return false;
    }

    private void AdvancePowertrain(Controller controller, float dt)
    {
        float targetSpeed = controller.Powered ? controller.SourceSpeed * controller.GearRatio : 0f;
        float ratio = MathF.Max(0.15f, MathF.Abs(controller.GearRatio));
        controller.OutputTorque = controller.Powered ? controller.SourceTorque * 0.88f / ratio : 0f;
        float error = targetSpeed - controller.ShaftSpeed;
        float torqueAcceleration = controller.OutputTorque / MathF.Max(0.25f, controller.Inertia);
        controller.ShaftSpeed += Math.Clamp(error * 4.5f, -torqueAcceleration, torqueAcceleration) * dt;
        float loadRatio = controller.OutputTorque > 0.001f
            ? Math.Clamp(controller.LastLoad / controller.OutputTorque, 0f, 3f)
            : 0f;
        controller.ShaftSpeed *= MathF.Exp(-(controller.PassiveDrag + controller.BrakeDrag + loadRatio * 1.35f) * dt);
        if (!controller.Powered && MathF.Abs(controller.ShaftSpeed) < 0.015f) controller.ShaftSpeed = 0f;
        controller.ShaftAngle = WrapAngle(controller.ShaftAngle + controller.ShaftSpeed * dt);
        controller.UnwrappedAngle += controller.ShaftSpeed * dt;
        controller.Stalled = controller.Powered && MathF.Abs(targetSpeed) > 0.2f &&
                             MathF.Abs(controller.ShaftSpeed) < MathF.Abs(targetSpeed) * 0.08f &&
                             controller.LastLoad >= controller.OutputTorque * 0.75f;
    }

    private bool Drive(Controller controller, float dt)
    {
        PhysicsGrid frame = controller.Grid;
        // Reactions go into the fixed support. They must not accumulate in a hidden,
        // drifting animation anchor and launch the complete machine later.
        frame.SetTransformAndVelocity(controller.AnchorPosition, controller.AnchorOrientation,
            Vector3.Zero, Vector3.Zero);
        int cycle = (int)MathF.Floor(controller.UnwrappedAngle / TwoPi);
        bool pulse = controller.Mode == MotionMode.ImpulsePad && cycle != controller.LastImpulseCycle;
        controller.LastImpulseCycle = cycle;
        controller.LastLoad = TransferMotion(controller, dt, pulse);
        return controller.LastLoad > 0.0001f;
    }

    private void Discover()
    {
        foreach ((ulong id, Controller controller) in _controllers.ToArray())
        {
            if (_world.PhysicsGrids.Get(id) != controller.Grid ||
                !TryAnalyze(controller.Grid, out MachineSpec spec) || !spec.Powered)
            {
                Release(controller);
                continue;
            }
            if (controller.TopologyVersion != controller.Grid.TopologyVersion) controller.Refresh(spec);
        }

        foreach (PhysicsGrid grid in _world.PhysicsGrids.Grids)
        {
            if (_controllers.ContainsKey(grid.Id) || grid.IsDeformable || grid.IsPhysicsGunHeld ||
                grid.IsPhysicsGunFrozen || !HasStaticAnchor(grid) ||
                !TryAnalyze(grid, out MachineSpec spec) || !spec.Powered) continue;
            grid.IsKinematicController = true;
            grid.IsSleeping = false;
            _controllers.Add(grid.Id, new Controller(grid, spec));
            _world.PhysicsGrids.NotifyTopologyChanged();
        }
    }

    private void Release(Controller controller)
    {
        controller.Grid.IsKinematicController = false;
        controller.Grid.Wake();
        _controllers.Remove(controller.Grid.Id);
        _world.PhysicsGrids.NotifyTopologyChanged();
    }

    private bool HasStaticAnchor(PhysicsGrid grid)
    {
        ReadOnlySpan<Vector3> directions =
        [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        foreach (PhysicsGridElement element in grid.Elements)
        {
            Vector3 half = (element.Maximum - element.Minimum) * 0.5f;
            foreach (Vector3 direction in directions)
            {
                float extent = MathF.Abs(direction.X) * half.X + MathF.Abs(direction.Y) * half.Y +
                               MathF.Abs(direction.Z) * half.Z;
                Vector3 sample = grid.LocalToWorld(element.Center + direction * (extent + 0.035f));
                if (_world.IsSolidForCollision((int)MathF.Floor(sample.X), (int)MathF.Floor(sample.Y),
                        (int)MathF.Floor(sample.Z))) return true;
            }
        }
        return false;
    }

    private bool TryAnalyze(PhysicsGrid grid, out MachineSpec spec)
    {
        PhysicsGridElement? actuatorElement = grid.Elements
            .Where(element => IsController(_blocks.Get(element.MaterialId)))
            .Select(element => (PhysicsGridElement?)element).FirstOrDefault();
        if (!actuatorElement.HasValue)
        {
            spec = default;
            return false;
        }

        var mechanicalByCell = grid.Elements
            .Where(element => IsTransmissionPart(_blocks.Get(element.MaterialId)))
            .GroupBy(element => (element.CellX, element.CellY, element.CellZ))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var connectedCells = new HashSet<(int X, int Y, int Z)>();
        var search = new Queue<(int X, int Y, int Z)>();
        var actuatorCell = (actuatorElement.Value.CellX, actuatorElement.Value.CellY, actuatorElement.Value.CellZ);
        connectedCells.Add(actuatorCell);
        search.Enqueue(actuatorCell);
        ReadOnlySpan<(int X, int Y, int Z)> directions =
        [(1,0,0), (-1,0,0), (0,1,0), (0,-1,0), (0,0,1), (0,0,-1)];
        while (search.TryDequeue(out (int X, int Y, int Z) cell))
        {
            foreach ((int dx, int dy, int dz) in directions)
            {
                var neighbor = (cell.X + dx, cell.Y + dy, cell.Z + dz);
                if (mechanicalByCell.ContainsKey(neighbor) && connectedCells.Add(neighbor)) search.Enqueue(neighbor);
            }
        }

        BlockDefinition actuator = _blocks.Get(actuatorElement.Value.MaterialId);
        float sourceTorque = 0f, weightedSpeed = 0f, gearRatio = 1f;
        float inertia = 0.45f + grid.Mass * 0.08f, brakeDrag = 0f;
        foreach ((int X, int Y, int Z) cell in connectedCells)
        foreach (PhysicsGridElement element in mechanicalByCell[cell])
        {
            BlockDefinition block = _blocks.Get(element.MaterialId);
            if (HasTag(block, "mechanical-source"))
            {
                float torque = TagValue(block, "source-torque:", 10f);
                sourceTorque += torque;
                weightedSpeed += TagValue(block, "source-speed:", 4f) * torque;
            }
            gearRatio *= TagValue(block, "gear-ratio:", 1f);
            inertia += TagValue(block, "mechanical-inertia:", 0f);
            brakeDrag += TagValue(block, "mechanical-brake:", 0f);
        }

        gearRatio = Math.Clamp(gearRatio, 0.12f, 6f);
        bool powered = sourceTorque > 0.001f;
        MotionMode mode = ResolveMode(actuator);
        Vector3 localAxis = mode == MotionMode.Lift ? Vector3.UnitY : ResolveOutputAxis(actuatorCell, connectedCells);
        spec = new MachineSpec(mode,
            Math.Clamp(TagValue(actuator, "motion-range:", mode == MotionMode.Rocker ? 0.65f : 2.5f), 0.1f, 8f),
            actuatorElement.Value.Center, localAxis, powered, sourceTorque,
            powered ? weightedSpeed / sourceTorque : 0f, gearRatio,
            Math.Clamp(inertia, 0.25f, 80f), Math.Clamp(brakeDrag, 0f, 18f));
        return true;
    }

    private static Vector3 ResolveOutputAxis((int X, int Y, int Z) actuator,
        IReadOnlySet<(int X, int Y, int Z)> network)
    {
        ReadOnlySpan<(int X, int Y, int Z, Vector3 Axis)> candidates =
        [
            (-1,0,0,Vector3.UnitX), (1,0,0,-Vector3.UnitX),
            (0,-1,0,Vector3.UnitY), (0,1,0,-Vector3.UnitY),
            (0,0,-1,Vector3.UnitZ), (0,0,1,-Vector3.UnitZ),
        ];
        foreach ((int dx, int dy, int dz, Vector3 output) in candidates)
            if (network.Contains((actuator.X + dx, actuator.Y + dy, actuator.Z + dz))) return output;
        return Vector3.UnitX;
    }

    private float TransferMotion(Controller controller, float dt, bool pulse)
    {
        PhysicsGrid frame = controller.Grid;
        Vector3 axis = Vector3.Normalize(frame.LocalDirectionToWorld(controller.LocalOutputAxis));
        Vector3 baseCenter = frame.LocalToWorld(controller.LocalActuatorCenter);
        float angle = controller.ShaftAngle;
        float travel = controller.Mode is MotionMode.Piston or MotionMode.Lift
            ? (1f - MathF.Cos(angle)) * 0.5f * controller.Range : 0f;
        Vector3 center = baseCenter + axis * (0.58f + travel);
        Vector3 half = controller.Mode == MotionMode.Conveyor ? new(1.05f, 0.36f, 0.72f) : new(0.58f);
        Vector3 minimum = center - half, maximum = center + half;
        Vector3 linearDrive = controller.Mode switch
        {
            MotionMode.Piston or MotionMode.Lift => axis *
                (MathF.Sin(angle) * 0.5f * controller.Range * controller.ShaftSpeed),
            MotionMode.Conveyor => axis * Math.Clamp(controller.ShaftSpeed * 0.9f, -5.5f, 5.5f),
            MotionMode.ImpulsePad when pulse => axis * Math.Clamp(MathF.Abs(controller.ShaftSpeed) * 1.8f, 2f, 7f),
            _ => Vector3.Zero,
        };
        Vector3 angularAxis = controller.Mode is MotionMode.Bearing or MotionMode.Rocker ? axis : Vector3.Zero;
        float angularSpeed = controller.Mode == MotionMode.Rocker
            ? MathF.Cos(angle) * controller.Range * controller.ShaftSpeed : controller.ShaftSpeed;
        float load = 0f;

        foreach (PhysicsGrid target in _world.PhysicsGrids.Grids)
        {
            if (target == frame || target.IsKinematicController || target.IsDeformable) continue;
            (Vector3 targetMin, Vector3 targetMax) = target.CalculateWorldBounds();
            if (!Overlaps(minimum, maximum, targetMin, targetMax)) continue;
            Vector3 contact = Vector3.Clamp(target.Position, minimum, maximum);
            Vector3 desired = linearDrive + Vector3.Cross(angularAxis * angularSpeed, contact - center);
            Vector3 relative = desired - target.VelocityAtPoint(contact);
            float mass = MathF.Min(target.Mass, 12f);
            Vector3 impulse = Vector3.Clamp(relative * mass * MathF.Min(1f, dt * 18f),
                new(-controller.OutputTorque), new(controller.OutputTorque));
            target.ApplyImpulseAtPoint(impulse, contact);
            load += impulse.Length() / MathF.Max(dt, 0.001f);
        }

        foreach (AnalyticSphere sphere in _world.AnalyticSpheres.Spheres)
        {
            if (sphere.IsKinematic) continue;
            Vector3 closest = Vector3.Clamp(sphere.Center, minimum, maximum);
            if (Vector3.DistanceSquared(sphere.Center, closest) > sphere.Radius * sphere.Radius) continue;
            Vector3 desired = linearDrive + Vector3.Cross(angularAxis * angularSpeed, closest - center);
            Vector3 delta = (desired - sphere.LinearVelocity) * MathF.Min(0.55f, dt * 10f);
            sphere.LinearVelocity += delta;
            sphere.IsSleeping = false;
            load += delta.Length() * MathF.Max(0.2f, sphere.Radius * sphere.Radius * sphere.Radius * 4f) /
                    MathF.Max(dt, 0.001f);
        }
        return load;
    }

    private static bool Overlaps(Vector3 aMin, Vector3 aMax, Vector3 bMin, Vector3 bMax) =>
        aMin.X <= bMax.X && aMax.X >= bMin.X && aMin.Y <= bMax.Y && aMax.Y >= bMin.Y &&
        aMin.Z <= bMax.Z && aMax.Z >= bMin.Z;
    private static bool IsController(BlockDefinition block) => HasTag(block, "motion-controller");
    private static bool IsTransmissionPart(BlockDefinition block) =>
        IsController(block) || HasTag(block, "mechanical") || HasTag(block, "mechanical-source") ||
        block.Tags.Any(tag => tag.StartsWith("gear-ratio:", StringComparison.OrdinalIgnoreCase) ||
                              tag.StartsWith("mechanical-inertia:", StringComparison.OrdinalIgnoreCase) ||
                              tag.StartsWith("mechanical-brake:", StringComparison.OrdinalIgnoreCase));
    private static bool HasTag(BlockDefinition block, string tag) =>
        block.Tags.Any(value => value.Equals(tag, StringComparison.OrdinalIgnoreCase));
    private static float TagValue(BlockDefinition block, string prefix, float fallback)
    {
        string? tag = block.Tags.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return tag is not null && float.TryParse(tag[prefix.Length..], NumberStyles.Float,
            CultureInfo.InvariantCulture, out float value) && float.IsFinite(value) ? value : fallback;
    }
    private static MotionMode ResolveMode(BlockDefinition block)
    {
        if (HasTag(block, "rotary")) return MotionMode.Bearing;
        if (HasTag(block, "lift")) return MotionMode.Lift;
        if (HasTag(block, "rocker")) return MotionMode.Rocker;
        if (HasTag(block, "conveyor")) return MotionMode.Conveyor;
        if (HasTag(block, "impulse-pad")) return MotionMode.ImpulsePad;
        return MotionMode.Piston;
    }
    private static float WrapAngle(float angle)
    {
        angle %= TwoPi;
        return angle < 0f ? angle + TwoPi : angle;
    }

    public readonly record struct MechanicalTelemetry(
        bool Powered, float Rpm, float AvailableTorque, float Load, float GearRatio, bool Stalled);
    private enum MotionMode { Piston, Lift, Bearing, Rocker, Conveyor, ImpulsePad }
    private readonly record struct MachineSpec(MotionMode Mode, float Range, Vector3 LocalActuatorCenter,
        Vector3 LocalOutputAxis, bool Powered, float SourceTorque, float SourceSpeed, float GearRatio,
        float Inertia, float BrakeDrag);

    private sealed class Controller
    {
        public PhysicsGrid Grid { get; }
        public Vector3 AnchorPosition { get; private set; }
        public Quaternion AnchorOrientation { get; private set; }
        public MotionMode Mode { get; private set; }
        public float Range { get; private set; }
        public Vector3 LocalActuatorCenter { get; private set; }
        public Vector3 LocalOutputAxis { get; private set; }
        public bool Powered { get; private set; }
        public float SourceTorque { get; private set; }
        public float SourceSpeed { get; private set; }
        public float GearRatio { get; private set; }
        public float Inertia { get; private set; }
        public float BrakeDrag { get; private set; }
        public float PassiveDrag { get; } = 0.18f;
        public int TopologyVersion { get; private set; }
        public float ShaftSpeed { get; set; }
        public float ShaftAngle { get; set; }
        public float UnwrappedAngle { get; set; }
        public float OutputTorque { get; set; }
        public float LastLoad { get; set; }
        public int LastImpulseCycle { get; set; }
        public bool Stalled { get; set; }

        public Controller(PhysicsGrid grid, MachineSpec spec)
        {
            Grid = grid;
            AnchorPosition = grid.Position;
            AnchorOrientation = grid.Orientation;
            Apply(spec);
        }
        public void Refresh(MachineSpec spec)
        {
            AnchorPosition = Grid.Position;
            AnchorOrientation = Grid.Orientation;
            ShaftAngle = 0f;
            UnwrappedAngle = 0f;
            Apply(spec);
        }
        private void Apply(MachineSpec spec)
        {
            Mode = spec.Mode;
            Range = spec.Range;
            LocalActuatorCenter = spec.LocalActuatorCenter;
            LocalOutputAxis = spec.LocalOutputAxis;
            Powered = spec.Powered;
            SourceTorque = spec.SourceTorque;
            SourceSpeed = spec.SourceSpeed;
            GearRatio = spec.GearRatio;
            Inertia = spec.Inertia;
            BrakeDrag = spec.BrakeDrag;
            TopologyVersion = Grid.TopologyVersion;
        }
    }
}
