using RTXSandbox.Engine;
using RTXSandbox.World;

namespace RTXSandbox.Modding;

/// <summary>
/// Internal gameplay event dispatch. This is engine plumbing, not a mod API:
/// BepInEx/Harmony plugins patch or reference the game assembly directly.
/// </summary>
internal sealed class GameEventHub
{
    private readonly object _sync = new();
    private readonly Dictionary<Type, List<HandlerEntry>> _handlers = new();
    private readonly VoxelWorld _world;
    private long _nextOrder;

    public GameEventHub(VoxelWorld world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    public void PublishWorldReady() => Publish(new WorldLoadedEvent(_world));

    public void Update(float deltaSeconds) => Publish(new WorldTickEvent(_world, deltaSeconds));

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler, int priority = 0) where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        HandlerEntry entry;
        lock (_sync)
        {
            entry = new HandlerEntry(priority, _nextOrder++, value => handler((TEvent)value));
            if (!_handlers.TryGetValue(typeof(TEvent), out List<HandlerEntry>? entries))
            {
                entries = new List<HandlerEntry>();
                _handlers.Add(typeof(TEvent), entries);
            }

            entries.Add(entry);
            entries.Sort(static (left, right) =>
            {
                int priorityOrder = right.Priority.CompareTo(left.Priority);
                return priorityOrder != 0 ? priorityOrder : left.Order.CompareTo(right.Order);
            });
        }

        return new EventSubscription(() => Unsubscribe(typeof(TEvent), entry));
    }

    public TEvent Publish<TEvent>(TEvent gameEvent) where TEvent : class
    {
        ArgumentNullException.ThrowIfNull(gameEvent);
        HandlerEntry[] snapshot;
        lock (_sync)
        {
            snapshot = _handlers.TryGetValue(typeof(TEvent), out List<HandlerEntry>? entries)
                ? entries.ToArray()
                : Array.Empty<HandlerEntry>();
        }

        foreach (HandlerEntry entry in snapshot)
        {
            try
            {
                entry.Handler(gameEvent);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"[Game event error] {typeof(TEvent).Name} handler failed; remaining handlers continue.\n{exception}");
            }
        }

        return gameEvent;
    }

    private void Unsubscribe(Type eventType, HandlerEntry entry)
    {
        lock (_sync)
        {
            if (!_handlers.TryGetValue(eventType, out List<HandlerEntry>? entries)) return;
            entries.Remove(entry);
            if (entries.Count == 0) _handlers.Remove(eventType);
        }
    }

    private sealed record HandlerEntry(int Priority, long Order, Action<object> Handler);

    private sealed class EventSubscription : IDisposable
    {
        private Action? _unsubscribe;

        public EventSubscription(Action unsubscribe) => _unsubscribe = unsubscribe;

        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
