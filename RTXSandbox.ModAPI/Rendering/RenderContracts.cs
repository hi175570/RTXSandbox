namespace RTXSandbox.ModAPI;

public readonly struct RenderBackendInfo
{
    public string Id { get; }
    public string DisplayName { get; }
    public bool Available { get; }
    public string Description { get; }

    public RenderBackendInfo(string id, string displayName, bool available, string description)
    {
        Id = id;
        DisplayName = displayName;
        Available = available;
        Description = description;
    }
}

/// <summary>Renderer discovery and switching service exposed through IModContext.GetService.</summary>
public interface IRenderBackendService
{
    string ActiveBackendId { get; }
    string ActiveBackendName { get; }
    IReadOnlyList<RenderBackendInfo> Backends { get; }
    bool TrySwitch(string backendId);
    void ResetHistory();
}

public sealed class BeforeRenderEvent : CancellableGameEvent
{
    public float DeltaSeconds { get; }

    public BeforeRenderEvent(float deltaSeconds) => DeltaSeconds = deltaSeconds;
}

public sealed class AfterRenderEvent
{
    public float DeltaSeconds { get; }

    public AfterRenderEvent(float deltaSeconds) => DeltaSeconds = deltaSeconds;
}
