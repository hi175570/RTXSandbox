namespace RTXSandbox.Android.TouchControls;

public sealed class VirtualInputState
{
    private readonly object _sync = new();
    private readonly Dictionary<string, int> _normalPressCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _toggleBindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pressedEdges = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _hardwareKeysByBinding = new(StringComparer.OrdinalIgnoreCase);
    private string? _hardwareRebindTarget;

    public float MoveX { get; private set; }
    public float MoveY { get; private set; }
    public float HardwareMoveX { get; private set; }
    public float HardwareMoveY { get; private set; }
    public float LookDeltaX { get; private set; }
    public float LookDeltaY { get; private set; }
    public event Action<string, bool>? BindingChanged;
    public event Action? HardwareBindingsChanged;

    public string? HardwareRebindTarget
    {
        get { lock (_sync) return _hardwareRebindTarget; }
    }

    public void BeginHardwareRebind(VirtualBindingKind kind, string binding)
    {
        lock (_sync) _hardwareRebindTarget = Key(kind, binding);
    }

    public bool TryCaptureHardwareKey(string physicalKey)
    {
        lock (_sync)
        {
            if (_hardwareRebindTarget is null) return false;
            bool unbind = physicalKey.Equals("Escape", StringComparison.OrdinalIgnoreCase) ||
                          physicalKey.Equals("Back", StringComparison.OrdinalIgnoreCase);
            if (!unbind)
            {
                foreach (string target in _hardwareKeysByBinding
                             .Where(pair => string.Equals(pair.Value, physicalKey, StringComparison.OrdinalIgnoreCase))
                             .Select(pair => pair.Key).ToArray())
                    _hardwareKeysByBinding.Remove(target);
            }
            _hardwareKeysByBinding[_hardwareRebindTarget] = unbind ? "Unbound" : physicalKey;
            _hardwareRebindTarget = null;
        }
        HardwareBindingsChanged?.Invoke();
        return true;
    }

    public string HardwareKeyLabel(VirtualBindingKind kind, string binding, string fallback)
    {
        lock (_sync)
            return _hardwareKeysByBinding.TryGetValue(Key(kind, binding), out string? physical)
                ? ShortPhysicalKey(physical)
                : fallback;
    }

    public bool TryResolveHardwareKey(string physicalKey, out VirtualBindingKind kind, out string binding)
    {
        kind = default;
        binding = string.Empty;
        lock (_sync)
        {
            string? target = _hardwareKeysByBinding.FirstOrDefault(pair =>
                string.Equals(pair.Value, physicalKey, StringComparison.OrdinalIgnoreCase)).Key;
            if (string.IsNullOrWhiteSpace(target))
            {
                return false;
            }
            int separator = target.IndexOf(':');
            if (separator <= 0 || !Enum.TryParse(target[..separator], out kind))
            {
                return false;
            }
            binding = target[(separator + 1)..];
            return true;
        }
    }

    public bool IsHardwareBindingRemapped(VirtualBindingKind kind, string binding)
    {
        lock (_sync) return _hardwareKeysByBinding.ContainsKey(Key(kind, binding));
    }

    public IReadOnlyDictionary<string, string> ExportHardwareBindings()
    {
        lock (_sync) return new Dictionary<string, string>(_hardwareKeysByBinding, StringComparer.OrdinalIgnoreCase);
    }

    public void ImportHardwareBindings(IReadOnlyDictionary<string, string>? bindings)
    {
        if (bindings is null) return;
        lock (_sync)
        {
            _hardwareKeysByBinding.Clear();
            foreach ((string target, string physical) in bindings)
            {
                if (target.Contains(':') && !string.IsNullOrWhiteSpace(physical))
                    _hardwareKeysByBinding[target] = physical;
            }
        }
    }

    public void ResetHardwareBindings()
    {
        lock (_sync)
        {
            _hardwareKeysByBinding.Clear();
            _hardwareRebindTarget = null;
        }
        HardwareBindingsChanged?.Invoke();
    }

    public bool IsDown(VirtualBindingKind kind, string binding)
    {
        lock (_sync)
        {
            string key = Key(kind, binding);
            return _normalPressCounts.ContainsKey(key) || _toggleBindings.Contains(key);
        }
    }

    public void Press(TouchControlDefinition control)
    {
        lock (_sync)
        {
            string key = Key(control.BindingKind, control.Binding);
            if (control.Mode == TouchButtonMode.Toggle)
            {
                bool enabled = !_toggleBindings.Remove(key);
                if (enabled) _toggleBindings.Add(key);
                if (enabled) _pressedEdges.Add(key);
                BindingChanged?.Invoke(key, enabled);
                return;
            }

            _normalPressCounts.TryGetValue(key, out int count);
            _normalPressCounts[key] = count + 1;
            if (count == 0)
            {
                _pressedEdges.Add(key);
                BindingChanged?.Invoke(key, true);
            }
        }
    }

    public void Release(TouchControlDefinition control)
    {
        lock (_sync)
        {
            if (control.Mode == TouchButtonMode.Toggle) return;
            string key = Key(control.BindingKind, control.Binding);
            if (!_normalPressCounts.TryGetValue(key, out int count)) return;
            if (count <= 1)
            {
                _normalPressCounts.Remove(key);
                BindingChanged?.Invoke(key, false);
            }
            else
            {
                _normalPressCounts[key] = count - 1;
            }
        }
    }

    public void SetMovement(float x, float y)
    {
        lock (_sync)
        {
            MoveX = Math.Clamp(x, -1f, 1f);
            MoveY = Math.Clamp(y, -1f, 1f);
        }
    }

    public void SetHardwareMovement(float x, float y)
    {
        lock (_sync)
        {
            HardwareMoveX = Math.Clamp(x, -1f, 1f);
            HardwareMoveY = Math.Clamp(y, -1f, 1f);
        }
    }

    public (float X, float Y) GetMovement()
    {
        lock (_sync)
        {
            return (
                Math.Clamp(MoveX + HardwareMoveX, -1f, 1f),
                Math.Clamp(MoveY + HardwareMoveY, -1f, 1f));
        }
    }

    public bool ConsumePressed(VirtualBindingKind kind, string binding)
    {
        lock (_sync) return _pressedEdges.Remove(Key(kind, binding));
    }

    public void AddLookDelta(float x, float y)
    {
        lock (_sync)
        {
            LookDeltaX += x;
            LookDeltaY += y;
        }
    }

    public (float X, float Y) ConsumeLookDelta()
    {
        lock (_sync)
        {
            (float x, float y) = (LookDeltaX, LookDeltaY);
            LookDeltaX = 0f;
            LookDeltaY = 0f;
            return (x, y);
        }
    }

    public void ReleaseAll()
    {
        lock (_sync)
        {
            foreach (string key in _normalPressCounts.Keys.ToArray()) BindingChanged?.Invoke(key, false);
            _normalPressCounts.Clear();
            _pressedEdges.Clear();
            MoveX = MoveY = HardwareMoveX = HardwareMoveY = LookDeltaX = LookDeltaY = 0f;
        }
    }

    private static string Key(VirtualBindingKind kind, string binding) => $"{kind}:{binding}";

    private static string ShortPhysicalKey(string value) => value
        .Replace("Keycode.", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("Left", "L", StringComparison.OrdinalIgnoreCase)
        .Replace("Right", "R", StringComparison.OrdinalIgnoreCase)
        .Replace("Dpad", "D-", StringComparison.OrdinalIgnoreCase);
}
