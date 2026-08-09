using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using System.Text.Json;
using RTXSandbox.Android.Game;
using RTXSandbox.Android.TouchControls;
using RTXSandbox.Infrastructure.Platform;

namespace RTXSandbox.Android;

// Xamarin's AlertDialog fluent methods are annotated nullable even though an
// initialized Builder returns itself on Android. Keep nullable analysis enabled
// everywhere else and suppress only that binding annotation mismatch here.
#pragma warning disable CS8602, CA1422 // Legacy system-bar flags are required for Android 8/9 fallback.

[Activity(
    Label = "RTX Sandbox",
    MainLauncher = true,
    ScreenOrientation = ScreenOrientation.Landscape,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden,
    Theme = "@android:style/Theme.Material.NoActionBar.Fullscreen")]
public sealed class MainActivity : Activity
{
    private static readonly string[] KeyboardBindings =
    {
        "W", "A", "S", "D", "Space", "Shift", "Ctrl", "E", "Q", "F", "V", "B",
        "Escape", "Tab", "Up", "Down", "Left", "Right",
        "1", "2", "3", "4", "5", "6", "7", "8", "9",
    };

    private static readonly string[] MouseBindings = { "Left", "Right", "Middle", "WheelUp", "WheelDown" };
    private static readonly string[] GameBindings = { "Move", "Crouch" };

    private TouchControlProfileStore _store = null!;
    private TouchControlView _controlView = null!;
    private VirtualInputState _input = null!;
    private Button _editButton = null!;
    private HorizontalScrollView _editorToolbar = null!;
    private AndroidGameSurface? _gameSurface;
    private TextView _statusText = null!;
    private bool _dirty;
    private readonly Dictionary<Keycode, TouchControlDefinition> _hardwareKeyControls = new();
    private readonly HashSet<Keycode> _pressedHardwareKeys = new();
    private readonly HashSet<string> _pressedMovementBindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TouchControlDefinition> _hardwareMouseControls = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pressedMouseButtons = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasMousePosition;
    private float _lastMouseX;
    private float _lastMouseY;
    private AlertDialog? _gameDialog;
    private bool _replacingGameDialog;
    private bool _diagnosticSession;
    private AndroidGameUiMode _currentUiMode = AndroidGameUiMode.Gameplay;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (Window is not null)
        {
            Window.SetFlags(WindowManagerFlags.Fullscreen, WindowManagerFlags.Fullscreen);
            Window.DecorView.SystemUiFlags = SystemUiFlags.Fullscreen |
                                               SystemUiFlags.HideNavigation |
                                               SystemUiFlags.ImmersiveSticky |
                                               SystemUiFlags.LayoutStable |
                                               SystemUiFlags.LayoutHideNavigation;
            if (OperatingSystem.IsAndroidVersionAtLeast(28))
            {
                Window.Attributes!.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
            }
        }

        string filesDirectory = FilesDir?.AbsolutePath ?? AppContext.BaseDirectory;
        IPlatformStorage platformStorage = new DirectoryPlatformStorage(filesDirectory);
        _store = new TouchControlProfileStore(filesDirectory);
        TouchControlSelfTest.Run();
        TouchControlProfile profile = _store.Load();
        _input = new VirtualInputState();
        string? hardwareBindings = GetSharedPreferences("rtxsandbox-input", FileCreationMode.Private)
            ?.GetString("hardware-bindings", null);
        if (!string.IsNullOrWhiteSpace(hardwareBindings))
        {
            try
            {
                _input.ImportHardwareBindings(
                    JsonSerializer.Deserialize<Dictionary<string, string>>(hardwareBindings));
            }
            catch (JsonException exception)
            {
                Log.Warn("RTXSandbox.Input", $"Ignoring invalid hardware bindings: {exception.Message}");
            }
        }
        _input.HardwareBindingsChanged += () =>
        {
            ReleaseHardwareInputs();
            _hardwareKeyControls.Clear();
            string json = JsonSerializer.Serialize(_input.ExportHardwareBindings());
            GetSharedPreferences("rtxsandbox-input", FileCreationMode.Private)
                ?.Edit()?.PutString("hardware-bindings", json)?.Apply();
        };
        _input.BindingChanged += (binding, down) =>
            Log.Debug("RTXSandbox.Input", $"{binding} {(down ? "Down" : "Up")}");
        _controlView = new TouchControlView(this, profile, _input);
        _controlView.ProfileChanged += () => _dirty = true;

        var root = new FrameLayout(this);
        root.SetBackgroundColor(Color.Rgb(9, 14, 23));
        try
        {
            (string baseContent, string contentPacks) = AndroidContentInstaller.Install(this);
            _gameSurface = new AndroidGameSurface(
                this, _input, baseContent, contentPacks, platformStorage, UpdateStatus);
            _gameSurface.UiStateChanged += HandleGameUiState;
            root.AddView(_gameSurface, new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        }
        catch (Exception exception)
        {
            Log.Error("RTXSandbox.Android", exception.ToString());
        }
        root.AddView(_controlView, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        _statusText = new TextView(this)
        {
            Text = _gameSurface is null ? "遊戲初始化失敗，請查看 Logcat" : "正在啟動 Classic Mobile…",
            TextSize = 13f,
        };
        _statusText.SetTextColor(Color.White);
        _statusText.SetBackgroundColor(Color.Argb(145, 9, 14, 23));
        _statusText.SetPadding(Dp(8), Dp(4), Dp(8), Dp(4));
        root.AddView(_statusText, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent,
            GravityFlags.Top | GravityFlags.Right));

        _editButton = MakeButton("編輯控制");
        _editButton.Click += (_, _) => EnterEditor();
        // Editing is entered from the Android ImGui pause menu. The old
        // permanent overlay button obscured gameplay and is not attached.
        _editButton.Visibility = ViewStates.Gone;

        _editorToolbar = CreateEditorToolbar();
        _editorToolbar.Visibility = ViewStates.Gone;
        root.AddView(_editorToolbar, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(62), GravityFlags.Bottom));
        SetContentView(root);
        bool debuggable = (ApplicationInfo?.Flags & ApplicationInfoFlags.Debuggable) != 0;
        _diagnosticSession = debuggable &&
                             Intent?.GetBooleanExtra("rtxsandbox.physics_smoke", false) == true;
        if (_diagnosticSession) _gameSurface?.RequestPhysicsSmokeTest();
    }

    protected override void OnPause()
    {
        SaveIfDirty();
        try
        {
            if (!_diagnosticSession) _gameSurface?.QueueWorldSave();
        }
        catch (Exception exception) { Log.Error("RTXSandbox.Save", exception.ToString()); }
        ReleaseHardwareInputs();
        _gameSurface?.OnPause();
        base.OnPause();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _gameSurface?.OnResume();
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus && Window is not null)
            Window.DecorView.SystemUiFlags |= SystemUiFlags.Fullscreen |
                                              SystemUiFlags.HideNavigation |
                                              SystemUiFlags.ImmersiveSticky;
    }

#pragma warning disable CS0672
    public override void OnBackPressed()
    {
        if (_controlView?.EditMode == true)
        {
            ExitEditor();
            return;
        }
        if (_gameDialog is not null)
        {
            DismissGameDialog(replacing: true);
            _gameSurface?.RequestUiMode(AndroidGameUiMode.Gameplay);
            return;
        }
        _gameSurface?.RequestUiMode(_currentUiMode switch
        {
            AndroidGameUiMode.Gameplay => AndroidGameUiMode.Pause,
            AndroidGameUiMode.Pause => AndroidGameUiMode.Gameplay,
            AndroidGameUiMode.MainMenu => AndroidGameUiMode.MainMenu,
            _ => AndroidGameUiMode.Pause,
        });
    }
#pragma warning restore CS0672

    private void UpdateStatus(string text) => RunOnUiThread(() =>
    {
        Log.Info("RTXSandbox.Status", text);
        if (_statusText is not null) _statusText.Text = text;
    });

    private void HandleGameUiState(AndroidGameUiState state) => RunOnUiThread(() =>
    {
        _currentUiMode = state.Mode;
        if (state.Mode == AndroidGameUiMode.TouchEditor)
        {
            _controlView.Visibility = ViewStates.Visible;
            EnterEditor();
            _gameSurface?.RequestUiMode(AndroidGameUiMode.Gameplay);
            return;
        }
        if (state.Mode == AndroidGameUiMode.Gameplay)
        {
            DismissGameDialog(replacing: true);
            _controlView.Visibility = ViewStates.Visible;
            _editButton.Visibility = ViewStates.Gone;
            return;
        }

        DismissGameDialog(replacing: true);
        _controlView.Visibility = ViewStates.Gone;
        _editButton.Visibility = ViewStates.Gone;
        // All normal game menus now render through Dear ImGui on the GLES
        // surface. Native AlertDialogs are no longer part of the active route.
    });

    private void ShowPauseDialog(AndroidGameUiState state)
    {
        string[] entries =
        {
            "繼續遊戲",
            "物品欄",
            "建造工具",
            "渲染器",
            "編輯觸控配置",
            "儲存世界",
            "離開遊戲",
        };
        ReplaceGameDialog(new AlertDialog.Builder(this)
            .SetTitle($"暫停 · {state.Renderer} · {state.SelectedBlock}")
            .SetItems(entries, (_, args) =>
            {
                switch (args.Which)
                {
                    case 0: _gameSurface?.RequestUiMode(AndroidGameUiMode.Gameplay); break;
                    case 1: _gameSurface?.RequestUiMode(AndroidGameUiMode.Inventory); break;
                    case 2: _gameSurface?.RequestUiMode(AndroidGameUiMode.BuildTools); break;
                    case 3: _gameSurface?.RequestUiMode(AndroidGameUiMode.RendererSelection); break;
                    case 4:
                        _gameSurface?.RequestUiMode(AndroidGameUiMode.Gameplay);
                        EnterEditor();
                        break;
                    case 5:
                        _gameSurface?.SaveWorld();
                        Toast.MakeText(this, "世界已儲存", ToastLength.Short)?.Show();
                        _gameSurface?.RequestUiMode(AndroidGameUiMode.Gameplay);
                        break;
                    case 6:
                        _gameSurface?.SaveWorld();
                        Finish();
                        break;
                }
            })
            .Create()!);
    }

    private void ShowBlockDialog(AndroidGameUiMode mode, string selectedBlock)
    {
        IReadOnlyList<AndroidBlockChoice> choices = _gameSurface?.BlockChoices ?? Array.Empty<AndroidBlockChoice>();
        string[] labels = choices.Select(choice =>
            string.Equals(choice.DisplayName, selectedBlock, StringComparison.Ordinal)
                ? $"✓ {choice.DisplayName}"
                : choice.DisplayName).ToArray();
        string title = mode == AndroidGameUiMode.Inventory ? "物品欄" : "建造工具 · 選擇方塊";
        ReplaceGameDialog(new AlertDialog.Builder(this)
            .SetTitle(title)
            .SetItems(labels, (_, args) =>
            {
                if ((uint)args.Which < choices.Count) _gameSurface?.SelectBlock(choices[args.Which].Id);
                _gameSurface?.RequestUiMode(AndroidGameUiMode.Gameplay);
            })
            .SetNegativeButton("返回", (_, _) => _gameSurface?.RequestUiMode(AndroidGameUiMode.Gameplay))
            .Create()!);
    }

    private void ShowRendererDialog(AndroidRendererMode selected)
    {
        IReadOnlyList<AndroidRendererChoice> choices = _gameSurface?.RendererChoices ?? Array.Empty<AndroidRendererChoice>();
        string[] labels = choices.Select(choice =>
            $"{(choice.Name == selected.ToString() ? "✓ " : string.Empty)}{choice.DisplayName}" +
            (choice.Available ? string.Empty : $" — 不支援：{choice.Reason}")).ToArray();
        ReplaceGameDialog(new AlertDialog.Builder(this)
            .SetTitle("渲染器能力")
            .SetItems(labels, (_, args) =>
            {
                if ((uint)args.Which >= choices.Count) return;
                AndroidRendererChoice choice = choices[args.Which];
                if (!choice.Available)
                {
                    Toast.MakeText(this, choice.Reason, ToastLength.Long)?.Show();
                    _gameSurface?.RequestUiMode(AndroidGameUiMode.Gameplay);
                    return;
                }
                if (Enum.TryParse(choice.Name, out AndroidRendererMode renderer))
                    _gameSurface?.SelectRenderer(renderer);
                _gameSurface?.RequestUiMode(AndroidGameUiMode.Gameplay);
            })
            .SetNegativeButton("返回", (_, _) => _gameSurface?.RequestUiMode(AndroidGameUiMode.Pause))
            .Create()!);
    }

    private void ReplaceGameDialog(AlertDialog dialog)
    {
        DismissGameDialog(replacing: true);
        _gameDialog = dialog;
        dialog.DismissEvent += (_, _) =>
        {
            if (!ReferenceEquals(_gameDialog, dialog)) return;
            _gameDialog = null;
        };
        dialog.CancelEvent += (_, _) =>
        {
            if (!IsFinishing) _gameSurface?.RequestUiMode(AndroidGameUiMode.Gameplay);
        };
        dialog.Show();
    }

    private void DismissGameDialog(bool replacing)
    {
        if (_gameDialog is null) return;
        _replacingGameDialog = replacing;
        AlertDialog dialog = _gameDialog;
        _gameDialog = null;
        dialog.Dismiss();
        _replacingGameDialog = false;
    }

    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e is null) return base.DispatchKeyEvent(e);
        if (e.Action == KeyEventActions.Down && e.RepeatCount == 0 &&
            _input.TryCaptureHardwareKey(e.KeyCode.ToString()))
            return true;
        if (e.KeyCode is Keycode.Escape or Keycode.Back)
        {
            if (e.Action == KeyEventActions.Down && e.RepeatCount == 0) OnBackPressed();
            return true;
        }
        TouchControlDefinition? control = HardwareControlFor(e.KeyCode);
        if (control is null) return base.DispatchKeyEvent(e);

        if (e.Action == KeyEventActions.Down)
        {
            if (_pressedHardwareKeys.Add(e.KeyCode)) _input.Press(control);
            UpdateMovementBinding(control, isPressed: true);
        }
        else if (e.Action == KeyEventActions.Up)
        {
            if (_pressedHardwareKeys.Remove(e.KeyCode)) _input.Release(control);
            UpdateMovementBinding(control, isPressed: false);
        }
        return true;
    }

    private void UpdateMovementBinding(TouchControlDefinition control, bool isPressed)
    {
        if (control.BindingKind != VirtualBindingKind.Keyboard) return;
        string binding = control.Binding;
        switch (binding.ToUpperInvariant())
        {
            case "W":
            case "A":
            case "S":
            case "D":
                if (isPressed) _pressedMovementBindings.Add(binding);
                else _pressedMovementBindings.Remove(binding);
                ApplyHardwareMovementFromPressedKeys();
                break;
        }
    }

    private void ApplyHardwareMovementFromPressedKeys()
    {
        float x = 0f;
        float y = 0f;
        if (_pressedMovementBindings.Contains("D")) x += 1f;
        if (_pressedMovementBindings.Contains("A")) x -= 1f;
        if (_pressedMovementBindings.Contains("W")) y += 1f;
        if (_pressedMovementBindings.Contains("S")) y -= 1f;
        if (x * x + y * y > 1f)
        {
            float invLen = 1f / MathF.Sqrt(x * x + y * y);
            x *= invLen;
            y *= invLen;
        }
        _input.SetHardwareMovement(x, y);
    }

    public override bool DispatchGenericMotionEvent(MotionEvent? e)
    {
        if (e is null) return base.DispatchGenericMotionEvent(e);
        int source = (int)e.Source;
        if ((source & (int)InputSourceType.ClassJoystick) != 0 && e.ActionMasked == MotionEventActions.Move)
        {
            float x = DeadZone(e.GetAxisValue(Axis.X));
            float y = -DeadZone(e.GetAxisValue(Axis.Y));
            float hatX = DeadZone(e.GetAxisValue(Axis.HatX));
            float hatY = -DeadZone(e.GetAxisValue(Axis.HatY));
            _input.SetHardwareMovement(MathF.Abs(hatX) > MathF.Abs(x) ? hatX : x,
                MathF.Abs(hatY) > MathF.Abs(y) ? hatY : y);
            _input.AddLookDelta(
                DeadZone(e.GetAxisValue(Axis.Z)) * 4.5f,
                DeadZone(e.GetAxisValue(Axis.Rz)) * 4.5f);
            return true;
        }

        if (IsMouseEvent(e))
        {
            UpdateMouseButton((int)e.ButtonState, 1, "Left");
            UpdateMouseButton((int)e.ButtonState, 2, "Right");
            UpdateMouseButton((int)e.ButtonState, 4, "Middle");
            if (e.ActionMasked is MotionEventActions.HoverMove or MotionEventActions.Move && !_controlView.EditMode)
            {
                if (_hasMousePosition)
                {
                    float deltaX = e.GetX() - _lastMouseX;
                    float deltaY = e.GetY() - _lastMouseY;
                    if (MathF.Abs(deltaX) < _controlView.Width * 0.45f &&
                        MathF.Abs(deltaY) < _controlView.Height * 0.45f)
                        _input.AddLookDelta(deltaX * 0.35f, deltaY * 0.35f);
                }
                _lastMouseX = e.GetX();
                _lastMouseY = e.GetY();
                _hasMousePosition = true;
            }
            if (e.ActionMasked == MotionEventActions.Scroll)
            {
                float wheel = e.GetAxisValue(Axis.Vscroll);
                if (MathF.Abs(wheel) > 0.01f) PulseMouse(wheel > 0f ? "WheelUp" : "WheelDown");
            }
            return true;
        }
        return base.DispatchGenericMotionEvent(e);
    }

    public override bool DispatchTouchEvent(MotionEvent? e)
    {
        if (e is not null && IsMouseEvent(e) && !_controlView.EditMode)
        {
            int state = (int)e.ButtonState;
            UpdateMouseButton(state, 1, "Left");
            UpdateMouseButton(state, 2, "Right");
            UpdateMouseButton(state, 4, "Middle");
            if (e.ActionMasked == MotionEventActions.Move)
            {
                if (_hasMousePosition)
                    _input.AddLookDelta((e.GetX() - _lastMouseX) * 0.35f, (e.GetY() - _lastMouseY) * 0.35f);
                _lastMouseX = e.GetX();
                _lastMouseY = e.GetY();
                _hasMousePosition = true;
            }
            return true;
        }
        return base.DispatchTouchEvent(e);
    }

    private TouchControlDefinition? HardwareControlFor(Keycode keycode)
    {
        if (_hardwareKeyControls.TryGetValue(keycode, out TouchControlDefinition? existing)) return existing;
        (VirtualBindingKind Kind, string Binding)? mapped;
        bool customResolved = false;
        if (_input.TryResolveHardwareKey(keycode.ToString(), out VirtualBindingKind customKind,
                out string customBinding))
        {
            mapped = (customKind, customBinding);
            customResolved = true;
        }
        else mapped = keycode switch
        {
            Keycode.W or Keycode.DpadUp => (VirtualBindingKind.Keyboard, "W"),
            Keycode.A or Keycode.DpadLeft => (VirtualBindingKind.Keyboard, "A"),
            Keycode.S or Keycode.DpadDown => (VirtualBindingKind.Keyboard, "S"),
            Keycode.D or Keycode.DpadRight => (VirtualBindingKind.Keyboard, "D"),
            Keycode.Space or Keycode.ButtonA => (VirtualBindingKind.Keyboard, "Space"),
            Keycode.ShiftLeft or Keycode.ShiftRight or Keycode.ButtonL1 => (VirtualBindingKind.GameAction, "Crouch"),
            Keycode.CtrlLeft or Keycode.CtrlRight or Keycode.ButtonB => (VirtualBindingKind.Keyboard, "Ctrl"),
            Keycode.E or Keycode.I or Keycode.Tab or Keycode.ButtonStart => (VirtualBindingKind.Keyboard, "E"),
            Keycode.V => (VirtualBindingKind.Keyboard, "V"),
            Keycode.B => (VirtualBindingKind.Keyboard, "B"),
            Keycode.Escape or Keycode.Back => (VirtualBindingKind.Keyboard, "Escape"),
            Keycode.ButtonX => (VirtualBindingKind.Mouse, "Left"),
            Keycode.ButtonY => (VirtualBindingKind.Mouse, "Right"),
            _ => null,
        };
        if (mapped is null) return null;
        if (!customResolved && _input.IsHardwareBindingRemapped(mapped.Value.Kind, mapped.Value.Binding)) return null;
        var control = new TouchControlDefinition
        {
            Id = $"hardware-{keycode}",
            BindingKind = mapped.Value.Kind,
            Binding = mapped.Value.Binding,
        };
        _hardwareKeyControls[keycode] = control;
        return control;
    }

    private void UpdateMouseButton(int state, int mask, string binding)
    {
        bool down = (state & mask) != 0;
        TouchControlDefinition control = MouseControl(binding);
        if (down && _pressedMouseButtons.Add(binding)) _input.Press(control);
        if (!down && _pressedMouseButtons.Remove(binding)) _input.Release(control);
    }

    private void PulseMouse(string binding)
    {
        TouchControlDefinition control = MouseControl(binding);
        _input.Press(control);
        _input.Release(control);
    }

    private TouchControlDefinition MouseControl(string binding)
    {
        if (_hardwareMouseControls.TryGetValue(binding, out TouchControlDefinition? control)) return control;
        control = new TouchControlDefinition
        {
            Id = $"hardware-mouse-{binding}",
            BindingKind = VirtualBindingKind.Mouse,
            Binding = binding,
        };
        _hardwareMouseControls[binding] = control;
        return control;
    }

    private void ReleaseHardwareInputs()
    {
        foreach (Keycode keycode in _pressedHardwareKeys.ToArray())
            if (HardwareControlFor(keycode) is { } control) _input.Release(control);
        _pressedHardwareKeys.Clear();
        _pressedMovementBindings.Clear();
        foreach (string binding in _pressedMouseButtons.ToArray()) _input.Release(MouseControl(binding));
        _pressedMouseButtons.Clear();
        _input.SetHardwareMovement(0f, 0f);
        _hasMousePosition = false;
    }

    private static float DeadZone(float value) => MathF.Abs(value) < 0.16f ? 0f : value;

    private static bool IsMouseEvent(MotionEvent e)
    {
        int source = (int)e.Source;
        bool mouseSource = (source & (int)InputSourceType.Mouse) == (int)InputSourceType.Mouse;
        bool mouseTool = e.PointerCount > 0 && (int)e.GetToolType(0) == 3; // TOOL_TYPE_MOUSE
        return mouseSource || mouseTool;
    }

    private HorizontalScrollView CreateEditorToolbar()
    {
        var scroll = new HorizontalScrollView(this);
        scroll.SetBackgroundColor(Color.Argb(235, 18, 27, 39));
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Dp(6), Dp(6), Dp(6), Dp(6));
        AddTool(row, "完成", ExitEditor);
        AddTool(row, "屬性", EditSelectedProperties);
        AddTool(row, "新增", AddControl);
        AddTool(row, "複製", () => _controlView.DuplicateSelected());
        AddTool(row, "刪除", () => _controlView.DeleteSelected());
        AddTool(row, "上移", () => _controlView.ChangeSelectedZ(1));
        AddTool(row, "下移", () => _controlView.ChangeSelectedZ(-1));
        AddTool(row, "視角", EditLookSettings);
        AddTool(row, "儲存", SaveNow);
        AddTool(row, "恢復預設", ConfirmRestoreDefaults);
        scroll.AddView(row);
        return scroll;
    }

    private void EnterEditor()
    {
        _controlView.SetEditMode(true);
        _editButton.Visibility = ViewStates.Gone;
        _editorToolbar.Visibility = ViewStates.Visible;
    }

    private void ExitEditor()
    {
        SaveNow();
        _controlView.SetEditMode(false);
        _editorToolbar.Visibility = ViewStates.Gone;
        _editButton.Visibility = ViewStates.Gone;
        _gameSurface?.RequestUiMode(AndroidGameUiMode.Pause);
    }

    private void AddControl()
    {
        TouchControlDefinition control = _controlView.AddButton();
        ShowProperties(control);
    }

    private void EditSelectedProperties()
    {
        if (_controlView.SelectedControl is not TouchControlDefinition control)
        {
            Toast.MakeText(this, "請先選取一個按鍵", ToastLength.Short)?.Show();
            return;
        }
        ShowProperties(control);
    }

    private void ShowProperties(TouchControlDefinition control)
    {
        var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        content.SetPadding(Dp(20), Dp(8), Dp(20), Dp(8));
        var label = new EditText(this) { Text = control.Label, Hint = "顯示文字" };
        content.AddView(label);

        var controlType = MakeSpinner(Enum.GetNames<TouchControlType>(), (int)control.Type);
        AddLabeled(content, "控制類型", controlType);

        var kind = MakeSpinner(Enum.GetNames<VirtualBindingKind>(), (int)control.BindingKind);
        AddLabeled(content, "輸入類型", kind);
        string[] initialBindings = BindingsFor(control.BindingKind);
        var binding = MakeSpinner(initialBindings, IndexOf(initialBindings, control.Binding));
        AddLabeled(content, "實際輸入綁定", binding);
        kind.ItemSelected += (_, _) =>
        {
            string[] choices = BindingsFor((VirtualBindingKind)kind.SelectedItemPosition);
            binding.Adapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleSpinnerDropDownItem, choices);
        };

        var mode = MakeSpinner(Enum.GetNames<TouchButtonMode>(), (int)control.Mode);
        AddLabeled(content, "按鍵模式", mode);
        SeekBar x = AddSlider(content, "位置 X", control.X, 0.01f, 0.99f);
        SeekBar y = AddSlider(content, "位置 Y", control.Y, 0.01f, 0.99f);
        SeekBar width = AddSlider(content, "寬度", control.Width, 0.025f, 0.70f);
        SeekBar height = AddSlider(content, "高度", control.Height, 0.025f, 0.70f);
        SeekBar scale = AddSlider(content, "縮放", control.Scale, 0.25f, 3f);
        SeekBar opacity = AddSlider(content, "透明度", control.Opacity, 0f, 1f);
        var visible = new CheckBox(this) { Text = "遊戲中顯示", Checked = control.Visible };
        content.AddView(visible);

        var scroll = new ScrollView(this);
        scroll.AddView(content);
        new AlertDialog.Builder(this)
            .SetTitle($"按鍵屬性：{control.Label}")
            .SetView(scroll)
            .SetNegativeButton("取消", (_, _) => { })
            .SetPositiveButton("套用", (_, _) =>
            {
                string enteredLabel = label.Text ?? string.Empty;
                control.Label = enteredLabel.Length <= 3 ? enteredLabel : enteredLabel[..3];
                control.Type = (TouchControlType)controlType.SelectedItemPosition;
                control.BindingKind = (VirtualBindingKind)kind.SelectedItemPosition;
                control.Binding = binding.SelectedItem?.ToString() ?? BindingsFor(control.BindingKind)[0];
                control.Mode = (TouchButtonMode)mode.SelectedItemPosition;
                control.X = SliderValue(x, 0.01f, 0.99f);
                control.Y = SliderValue(y, 0.01f, 0.99f);
                control.Width = SliderValue(width, 0.025f, 0.70f);
                control.Height = SliderValue(height, 0.025f, 0.70f);
                control.Scale = SliderValue(scale, 0.25f, 3f);
                control.Opacity = SliderValue(opacity, 0f, 1f);
                control.Visible = visible.Checked;
                _controlView.NotifySelectedPropertiesChanged();
            })
            .Show();
    }

    private void EditLookSettings()
    {
        TouchControlProfile profile = _controlView.Profile;
        var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        content.SetPadding(Dp(20), Dp(8), Dp(20), Dp(8));
        SeekBar horizontal = AddSlider(content, "水平靈敏度", profile.HorizontalLookSensitivity, 0.01f, 1.5f);
        SeekBar vertical = AddSlider(content, "垂直靈敏度", profile.VerticalLookSensitivity, 0.01f, 1.5f);
        var invert = new CheckBox(this) { Text = "反轉 Y 軸", Checked = profile.InvertLookY };
        content.AddView(invert);
        new AlertDialog.Builder(this)
            .SetTitle("視角觸控")
            .SetView(content)
            .SetNegativeButton("取消", (_, _) => { })
            .SetPositiveButton("套用", (_, _) =>
            {
                profile.HorizontalLookSensitivity = SliderValue(horizontal, 0.01f, 1.5f);
                profile.VerticalLookSensitivity = SliderValue(vertical, 0.01f, 1.5f);
                profile.InvertLookY = invert.Checked;
                _controlView.NotifySelectedPropertiesChanged();
            })
            .Show();
    }

    private void ConfirmRestoreDefaults()
    {
        new AlertDialog.Builder(this)
            .SetTitle("恢復預設控制")
            .SetMessage("目前布局會被預設布局取代。")
            .SetNegativeButton("取消", (_, _) => { })
            .SetPositiveButton("恢復", (_, _) =>
            {
                _controlView.SetProfile(TouchControlProfile.CreateDefault());
                _dirty = true;
                SaveNow();
            })
            .Show();
    }

    private void SaveNow()
    {
        try
        {
            _store.Save(_controlView.Profile);
            _dirty = false;
            Toast.MakeText(this, "控制布局已儲存", ToastLength.Short)?.Show();
        }
        catch (IOException exception)
        {
            Toast.MakeText(this, $"儲存失敗：{exception.Message}", ToastLength.Long)?.Show();
        }
    }

    private void SaveIfDirty()
    {
        if (_dirty) SaveNow();
    }

    private void AddTool(LinearLayout row, string label, Action action)
    {
        Button button = MakeButton(label);
        button.Click += (_, _) => action();
        row.AddView(button, new LinearLayout.LayoutParams(Dp(104), Dp(50)) { RightMargin = Dp(5) });
    }

    private Button MakeButton(string text)
    {
        var button = new Button(this) { Text = text, TextSize = 13f };
        button.SetAllCaps(false);
        return button;
    }

    private Spinner MakeSpinner(string[] values, int selected)
    {
        var spinner = new Spinner(this);
        spinner.Adapter = new ArrayAdapter<string>(this,
            global::Android.Resource.Layout.SimpleSpinnerDropDownItem, values);
        spinner.SetSelection(Math.Clamp(selected, 0, Math.Max(0, values.Length - 1)));
        return spinner;
    }

    private void AddLabeled(LinearLayout parent, string label, View view)
    {
        parent.AddView(new TextView(this) { Text = label, TextSize = 13f });
        parent.AddView(view);
    }

    private SeekBar AddSlider(LinearLayout parent, string label, float value, float minimum, float maximum)
    {
        parent.AddView(new TextView(this) { Text = label, TextSize = 13f });
        var slider = new SeekBar(this) { Max = 1000, Progress = ToProgress(value, minimum, maximum) };
        parent.AddView(slider);
        return slider;
    }

    private static int ToProgress(float value, float minimum, float maximum) =>
        (int)MathF.Round(Math.Clamp((value - minimum) / (maximum - minimum), 0f, 1f) * 1000f);

    private static float SliderValue(SeekBar slider, float minimum, float maximum) =>
        minimum + slider.Progress / 1000f * (maximum - minimum);

    private static int IndexOf(string[] values, string value)
    {
        int index = Array.FindIndex(values, item => item.Equals(value, StringComparison.OrdinalIgnoreCase));
        return Math.Max(index, 0);
    }

    private static string[] BindingsFor(VirtualBindingKind kind) => kind switch
    {
        VirtualBindingKind.Mouse => MouseBindings,
        VirtualBindingKind.GameAction => GameBindings,
        _ => KeyboardBindings,
    };

    private int Dp(int value) => (int)MathF.Round(value * Resources!.DisplayMetrics!.Density);
}

#pragma warning restore CS8602, CA1422
