using System.Numerics;
using RTXSandbox.World;

namespace RTXSandbox.Gameplay;

/// <summary>Turns rigid/analytic water entry into small shared fluid-level ripples.</summary>
public sealed class WaterPhysicsInteractionSystem
{
    private readonly VoxelWorld _world;
    private readonly WaterFlowSimulation _water;
    private readonly Dictionary<ulong, State> _gridStates = new();
    private readonly Dictionary<ulong, State> _sphereStates = new();

    public WaterPhysicsInteractionSystem(VoxelWorld world, WaterFlowSimulation water)
    {
        _world = world;
        _water = water;
    }

    public void Update(float deltaSeconds)
    {
        foreach (PhysicsGrid grid in _world.PhysicsGrids.Grids)
        {
            (Vector3 minimum, Vector3 maximum) = grid.CalculateWorldBounds();
            Vector3 probe = new((minimum.X + maximum.X) * 0.5f, minimum.Y + 0.18f,
                (minimum.Z + maximum.Z) * 0.5f);
            bool submerged = IsWater(probe);
            State state = _gridStates.GetValueOrDefault(grid.Id);
            float cooldown = MathF.Max(0f, state.Cooldown - deltaSeconds);
            float speed = grid.VelocityAtPoint(probe).Length();
            if (submerged && cooldown <= 0f && (!state.Submerged || speed > 1.1f))
            {
                _water.Disturb(probe, Math.Clamp(speed + grid.Mass * 0.08f, 0.5f, 10f));
                cooldown = 0.28f;
            }
            _gridStates[grid.Id] = new State(submerged, cooldown);
        }
        foreach (ulong stale in _gridStates.Keys.Where(id => _world.PhysicsGrids.Get(id) is null).ToArray())
            _gridStates.Remove(stale);

        foreach (AnalyticSphere sphere in _world.AnalyticSpheres.Spheres.Where(value => !value.IsKinematic))
        {
            Vector3 probe = sphere.Center - Vector3.UnitY * sphere.Radius * 0.65f;
            bool submerged = IsWater(probe);
            State state = _sphereStates.GetValueOrDefault(sphere.Id);
            float cooldown = MathF.Max(0f, state.Cooldown - deltaSeconds);
            float speed = sphere.LinearVelocity.Length();
            if (submerged && cooldown <= 0f && (!state.Submerged || speed > 1.1f))
            {
                _water.Disturb(probe, Math.Clamp(speed + sphere.Radius, 0.5f, 8f));
                cooldown = 0.28f;
            }
            _sphereStates[sphere.Id] = new State(submerged, cooldown);
        }
        foreach (ulong stale in _sphereStates.Keys.Where(id => _world.AnalyticSpheres.Get(id) is null).ToArray())
            _sphereStates.Remove(stale);
    }

    private bool IsWater(Vector3 position) => _world.GetFluidAmount(
        (int)MathF.Floor(position.X),
        (int)MathF.Floor(position.Y),
        (int)MathF.Floor(position.Z)) > 0;

    private readonly record struct State(bool Submerged, float Cooldown);
}
