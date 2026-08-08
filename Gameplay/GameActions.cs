namespace RTXSandbox.Gameplay;

/// <summary>
/// Platform-neutral actions. Hosts translate keyboard, mouse, touch and gamepad
/// input into these actions; gameplay code does not need to know the source.
/// </summary>
public enum GameAction
{
    Jump,
    Crouch,
    Sprint,
    Attack,
    Use,
    Inventory,
    Pause,
    BuildTools,
    PrecisionMode,
    NextRenderer,
}

public static class GameActionNames
{
    public static string Binding(GameAction action) => action.ToString();
}
