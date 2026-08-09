namespace RTXSandbox.Android.Game;

public enum AndroidGameUiMode
{
    Gameplay,
    MainMenu,
    CreateWorld,
    Pause,
    Inventory,
    BuildTools,
    Settings,
    RendererSelection,
    Achievements,
    Language,
    Controls,
    Mods,
    TouchEditor,
}

public enum AndroidRendererMode
{
    Classic,
    PbrForward,
    Deferred,
    Hybrid,
    ComputePathTracing,
    HardwareRayTracing,
}

public sealed record AndroidBlockChoice(ushort Id, string Key, string DisplayName);

public sealed record AndroidRendererChoice(
    string Name,
    string DisplayName,
    bool Available,
    string Reason);

public sealed record AndroidGameUiState(
    AndroidGameUiMode Mode,
    string SelectedBlock,
    bool PrecisionMode,
    AndroidRendererMode Renderer);
