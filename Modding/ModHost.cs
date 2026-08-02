using RTXSandbox.ModAPI;
using RTXSandbox.Gameplay;
using RTXSandbox.World;

namespace RTXSandbox.Modding;

public sealed class ModHost
{
    private readonly ModGameEvents _events;
    private readonly ModEventBus _eventBus;
    private readonly ModContext _context;
    private readonly ModServiceProvider _services = new();

    public ModHost(
        object game,
        BlockRegistry blocks,
        ItemRegistry items,
        RecipeRegistry recipes,
        BuildToolRegistry tools,
        VoxelWorld world)
    {
        var logger = new ConsoleModLogger();
        _events = new ModGameEvents(logger);
        _eventBus = new ModEventBus(logger);
        _services.Register(game);
        _services.Register(blocks);
        _services.Register(items);
        _services.Register(recipes);
        _services.Register(tools);
        _services.Register(world);
        _services.Register(_eventBus);
        _context = new ModContext(game, blocks, items, recipes, tools, world, _events, _eventBus, logger, _services);
        world.DetailedVoxelChanged += _events.PublishVoxelChanged;
    }

    public void RegisterService(object service) => _services.Register(service);

    public void AttachApi()
    {
        SandboxApi.AttachHost(_context);
    }

    public void PublishWorldReady()
    {
        _eventBus.Publish(new WorldLoadedEvent(_context.World));
        _events.PublishWorldReady(_context.World);
    }

    public void Update(float deltaSeconds)
    {
        _eventBus.Publish(new WorldTickEvent(_context.World, deltaSeconds));
        _events.PublishUpdate(deltaSeconds);
    }

    public TEvent Publish<TEvent>(TEvent gameEvent) where TEvent : class => _eventBus.Publish(gameEvent);

    private sealed class ModContext : IModContext
    {
        public IBlockRegistry Blocks { get; }
        public IItemRegistry Items { get; }
        public IRecipeRegistry Recipes { get; }
        public IBuildToolRegistry Tools { get; }
        public IGameEvents Events { get; }
        public IGameEventBus EventBus { get; }
        public IVoxelWorld World { get; }
        public IModLogger Log { get; }
        public object Game { get; }
        public IServiceProvider Services { get; }
        public string GameVersion => "0.13.0";

        public ModContext(
            object game,
            IBlockRegistry blocks,
            IItemRegistry items,
            IRecipeRegistry recipes,
            IBuildToolRegistry tools,
            IVoxelWorld world,
            IGameEvents events,
            IGameEventBus eventBus,
            IModLogger log,
            IServiceProvider services)
        {
            Game = game;
            Blocks = blocks;
            Items = items;
            Recipes = recipes;
            Tools = tools;
            World = world;
            Events = events;
            EventBus = eventBus;
            Log = log;
            Services = services;
        }

        public T? GetService<T>() where T : class => Services.GetService(typeof(T)) as T;
    }

    private sealed class ModEventBus : IGameEventBus
    {
        private readonly object _sync = new();
        private readonly Dictionary<Type, List<HandlerEntry>> _handlers = new();
        private readonly IModLogger _log;
        private long _nextOrder;

        public ModEventBus(IModLogger log) => _log = log;

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
                    _log.Error($"A mod failed while handling {typeof(TEvent).Name}; other handlers will continue.", exception);
                }
            }
            return gameEvent;
        }

        private void Unsubscribe(Type eventType, HandlerEntry entry)
        {
            lock (_sync)
            {
                if (_handlers.TryGetValue(eventType, out List<HandlerEntry>? entries))
                {
                    entries.Remove(entry);
                    if (entries.Count == 0)
                    {
                        _handlers.Remove(eventType);
                    }
                }
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

    private sealed class ModServiceProvider : IServiceProvider
    {
        private readonly object _sync = new();
        private readonly Dictionary<Type, object> _services = new();

        public void Register(object service)
        {
            ArgumentNullException.ThrowIfNull(service);
            lock (_sync)
            {
                _services[service.GetType()] = service;
            }
        }

        public object? GetService(Type serviceType)
        {
            lock (_sync)
            {
                if (_services.TryGetValue(serviceType, out object? exact))
                {
                    return exact;
                }

                return _services.Values.LastOrDefault(service => serviceType.IsInstanceOfType(service));
            }
        }
    }

    private sealed class ModGameEvents : IGameEvents
    {
        private readonly IModLogger _log;

        public event Action<IVoxelWorld>? WorldReady;
        public event Action<float>? Updating;
        public event Action<VoxelChangedEvent>? VoxelChanged;

        public ModGameEvents(IModLogger log)
        {
            _log = log;
        }

        public void PublishWorldReady(IVoxelWorld world) => InvokeSafely(WorldReady, world, "WorldReady");
        public void PublishUpdate(float deltaSeconds) => InvokeSafely(Updating, deltaSeconds, "Updating");
        public void PublishVoxelChanged(VoxelChangedEvent change) => InvokeSafely(VoxelChanged, change, "VoxelChanged");

        private void InvokeSafely<T>(Action<T>? handlers, T argument, string eventName)
        {
            if (handlers is null)
            {
                return;
            }

            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<T>)handler)(argument);
                }
                catch (Exception exception)
                {
                    _log.Error($"A mod failed while handling {eventName}; other mods will continue.", exception);
                }
            }
        }
    }

    private sealed class ConsoleModLogger : IModLogger
    {
        public void Info(string message) => Console.WriteLine($"[Mod] {message}");
        public void Warning(string message) => Console.WriteLine($"[Mod warning] {message}");
        public void Error(string message, Exception? exception = null) =>
            Console.Error.WriteLine($"[Mod error] {message}{(exception is null ? string.Empty : $"\n{exception}")}");
    }
}
