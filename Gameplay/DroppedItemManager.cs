using System.Numerics;
using RTXSandbox.ModAPI;
using RTXSandbox.World;

namespace RTXSandbox.Gameplay;

public sealed class DroppedItem
{
    public string ItemKey { get; }
    public int Count { get; internal set; }
    public Vector3 Position { get; internal set; }
    public Vector3 Velocity { get; internal set; }
    public float AgeSeconds { get; internal set; }

    internal DroppedItem(string itemKey, int count, Vector3 position, Vector3 velocity)
    {
        ItemKey = itemKey;
        Count = count;
        Position = position;
        Velocity = velocity;
    }
}

public sealed class DroppedItemManager
{
    private readonly List<DroppedItem> _drops = new();
    private readonly VoxelWorld _world;
    private readonly ItemRegistry _items;
    private readonly PlayerInventory _inventory;
    private readonly Func<ItemPickupEvent, ItemPickupEvent> _publishPickup;

    public event Action<string, int>? PickedUp;
    public IReadOnlyList<DroppedItem> Items => _drops.ToArray();

    public DroppedItemManager(
        VoxelWorld world,
        ItemRegistry items,
        PlayerInventory inventory,
        Func<ItemPickupEvent, ItemPickupEvent> publishPickup)
    {
        _world = world;
        _items = items;
        _inventory = inventory;
        _publishPickup = publishPickup;
    }

    public bool Spawn(string itemKey, int count, Vector3 position, Vector3? velocity = null)
    {
        if (count <= 0 || !_items.TryGet(itemKey, out _))
        {
            return false;
        }
        _drops.Add(new DroppedItem(
            itemKey,
            count,
            position,
            velocity ?? new Vector3(0f, 1.8f, 0f)));
        return true;
    }

    public void Update(float deltaSeconds, Vector3 playerPosition)
    {
        deltaSeconds = Math.Clamp(deltaSeconds, 0f, 0.05f);
        for (int index = _drops.Count - 1; index >= 0; index--)
        {
            DroppedItem drop = _drops[index];
            drop.AgeSeconds += deltaSeconds;
            drop.Velocity += new Vector3(0f, -13f * deltaSeconds, 0f);
            Vector3 candidate = drop.Position + drop.Velocity * deltaSeconds;

            int groundX = (int)MathF.Floor(candidate.X);
            int groundY = (int)MathF.Floor(candidate.Y - 0.16f);
            int groundZ = (int)MathF.Floor(candidate.Z);
            if (drop.Velocity.Y < 0f && _world.IsSolidForCollision(groundX, groundY, groundZ))
            {
                candidate.Y = groundY + 1.16f;
                drop.Velocity = new Vector3(drop.Velocity.X * 0.65f, 0f, drop.Velocity.Z * 0.65f);
            }
            drop.Position = candidate;

            if (drop.AgeSeconds >= 0.25f &&
                Vector3.DistanceSquared(drop.Position, playerPosition + Vector3.UnitY * 0.8f) <= 2.25f)
            {
                int originalCount = drop.Count;
                ItemPickupEvent pickup = _publishPickup(new ItemPickupEvent(drop.ItemKey, drop.Count));
                if (!pickup.IsCancelled && pickup.Count > 0)
                {
                    int remaining = _inventory.Add(drop.ItemKey, pickup.Count);
                    int pickedUp = pickup.Count - remaining;
                    drop.Count = Math.Max(0, originalCount - pickedUp);
                    if (pickedUp > 0)
                    {
                        PickedUp?.Invoke(drop.ItemKey, pickedUp);
                    }
                }
            }

            if (drop.Count <= 0 || drop.AgeSeconds >= 300f || drop.Position.Y < -16f)
            {
                _drops.RemoveAt(index);
            }
        }
    }

    public void Clear() => _drops.Clear();
}
