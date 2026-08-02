using RTXSandbox.ModAPI;

namespace RTXSandbox.Gameplay;

public sealed class GameObjective
{
    public string Id { get; }
    public string Title { get; }
    public string Description { get; }
    public bool Completed { get; internal set; }

    internal GameObjective(string id, string title, string description)
    {
        Id = id;
        Title = title;
        Description = description;
    }
}

public sealed class ProgressionTracker
{
    private readonly List<GameObjective> _objectives = new()
    {
        new("gather_wood", "Gather wood", "Find a tree and collect at least one wood block."),
        new("craft_planks", "Make planks", "Turn wood into wood planks in the inventory crafting tab."),
        new("build_workbench", "Build a workbench", "Craft and place a workbench near your shelter."),
        new("craft_stone_pickaxe", "Upgrade your tools", "Use a nearby workbench to craft a stone pickaxe."),
        new("place_light", "Light the shelter", "Place any emissive block before night."),
        new("survive_night", "Survive the night", "Stay alive from nightfall until the next sunrise."),
    };

    private readonly PlayerInventory _inventory;
    private readonly Func<ObjectiveCompletedEvent, ObjectiveCompletedEvent> _publish;
    private bool _sawNight;

    public event Action<GameObjective>? Completed;
    public IReadOnlyList<GameObjective> Objectives => _objectives;
    public GameObjective? Current => _objectives.FirstOrDefault(objective => !objective.Completed);
    public bool AllCompleted => _objectives.All(objective => objective.Completed);
    public bool SawNight => _sawNight;

    public ProgressionTracker(
        PlayerInventory inventory,
        Func<ObjectiveCompletedEvent, ObjectiveCompletedEvent> publish)
    {
        _inventory = inventory;
        _publish = publish;
    }

    public void Update(float timeOfDay)
    {
        if (_inventory.Count("rtxsandbox:wood") > 0) Complete("gather_wood");
        if (_inventory.Count("rtxsandbox:planks") > 0) Complete("craft_planks");
        if (_inventory.Count("rtxsandbox:stone_pickaxe") > 0) Complete("craft_stone_pickaxe");

        if (timeOfDay >= 18f || timeOfDay < 5.5f)
        {
            _sawNight = true;
        }
        else if (_sawNight && timeOfDay >= 6f && timeOfDay < 9f)
        {
            Complete("survive_night");
        }
    }

    public void NotifyCrafted(RecipeDefinition recipe, ItemStack output)
    {
        if (string.Equals(output.ItemKey, "rtxsandbox:planks", StringComparison.OrdinalIgnoreCase))
        {
            Complete("craft_planks");
        }
        if (string.Equals(output.ItemKey, "rtxsandbox:workbench", StringComparison.OrdinalIgnoreCase))
        {
            // Placement remains the actual completion condition.
        }
        if (string.Equals(output.ItemKey, "rtxsandbox:stone_pickaxe", StringComparison.OrdinalIgnoreCase))
        {
            Complete("craft_stone_pickaxe");
        }
    }

    public void NotifyBlockPlaced(BlockDefinition block)
    {
        if (string.Equals(block.Key, "rtxsandbox:workbench", StringComparison.OrdinalIgnoreCase))
        {
            Complete("build_workbench");
        }
        if (block.Emission.LengthSquared() > 0.01f)
        {
            Complete("place_light");
        }
    }

    public IReadOnlyList<string> SnapshotCompletedIds() => _objectives
        .Where(objective => objective.Completed)
        .Select(objective => objective.Id)
        .ToArray();

    public void Restore(IEnumerable<string> completedIds, bool sawNight)
    {
        var completed = new HashSet<string>(completedIds, StringComparer.OrdinalIgnoreCase);
        foreach (GameObjective objective in _objectives)
        {
            objective.Completed = completed.Contains(objective.Id);
        }
        _sawNight = sawNight;
    }

    private void Complete(string id)
    {
        GameObjective? objective = _objectives.FirstOrDefault(candidate => candidate.Id == id);
        if (objective is null || objective.Completed)
        {
            return;
        }
        objective.Completed = true;
        _publish(new ObjectiveCompletedEvent(objective.Id, objective.Title));
        Completed?.Invoke(objective);
    }
}
