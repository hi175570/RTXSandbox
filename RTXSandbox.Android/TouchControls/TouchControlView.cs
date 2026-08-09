using Android.Content;
using Android.Graphics;
using Android.Views;

namespace RTXSandbox.Android.TouchControls;

public sealed class TouchControlView : View
{
    private enum CaptureKind
    {
        Button,
        Joystick,
        Look,
        EditDrag,
        EditResize,
    }

    private sealed class PointerCapture
    {
        public required int PointerId { get; init; }
        public required CaptureKind Kind { get; init; }
        public TouchControlDefinition? Control { get; init; }
        public float StartX { get; init; }
        public float StartY { get; init; }
        public float LastX { get; set; }
        public float LastY { get; set; }
        public bool LongPressCancelled { get; set; }
        public bool LongPressTriggered { get; set; }
    }

    private readonly Paint _fill = new(PaintFlags.AntiAlias);
    private readonly Paint _stroke = new(PaintFlags.AntiAlias) { StrokeWidth = 3f };
    private readonly Paint _text = new(PaintFlags.AntiAlias) { TextAlign = Paint.Align.Center };
    private readonly Dictionary<int, PointerCapture> _captures = new();
    private readonly TouchControlDefinition _dpadCrouchControl = new()
    {
        Id = "dpad-crouch",
        Type = TouchControlType.Button,
        Label = "SHF",
        BindingKind = VirtualBindingKind.GameAction,
        Binding = "Crouch",
    };
    private readonly TouchControlDefinition _lookBreakControl = new()
    {
        Id = "look-long-press-break",
        Type = TouchControlType.Button,
        Label = "LMB",
        BindingKind = VirtualBindingKind.Mouse,
        Binding = "Left",
    };
    private TouchControlDefinition? _selected;

    public TouchControlView(Context context, TouchControlProfile profile, VirtualInputState input) : base(context)
    {
        Profile = profile;
        Input = input;
        Focusable = true;
        SetBackgroundColor(Color.Transparent);
    }

    public TouchControlProfile Profile { get; private set; }
    public VirtualInputState Input { get; }
    public bool EditMode { get; private set; }
    public TouchControlDefinition? SelectedControl => _selected;
    public event Action<TouchControlDefinition?>? SelectionChanged;
    public event Action? ProfileChanged;

    public void SetProfile(TouchControlProfile profile)
    {
        CancelAllTouches();
        Profile = profile;
        Select(null);
        Invalidate();
    }

    public void SetEditMode(bool enabled)
    {
        if (EditMode == enabled) return;
        CancelAllTouches();
        EditMode = enabled;
        if (!enabled) Select(null);
        Invalidate();
    }

    public TouchControlDefinition AddButton()
    {
        var control = new TouchControlDefinition
        {
            X = 0.5f,
            Y = 0.5f,
            Label = "F",
            BindingKind = VirtualBindingKind.Keyboard,
            Binding = "F",
            Opacity = 0.55f,
            ZIndex = Profile.Controls.Count == 0 ? 1 : Profile.Controls.Max(item => item.ZIndex) + 1,
        };
        Profile.Controls.Add(control);
        Select(control);
        Changed();
        return control;
    }

    public TouchControlDefinition? DuplicateSelected()
    {
        if (_selected is null) return null;
        TouchControlDefinition duplicate = _selected.Clone();
        duplicate.ZIndex = Profile.Controls.Max(item => item.ZIndex) + 1;
        Profile.Controls.Add(duplicate);
        Select(duplicate);
        Changed();
        return duplicate;
    }

    public bool DeleteSelected()
    {
        if (_selected is null) return false;
        bool removed = Profile.Controls.Remove(_selected);
        Select(null);
        if (removed) Changed();
        return removed;
    }

    public void ChangeSelectedZ(int delta)
    {
        if (_selected is null) return;
        _selected.ZIndex += delta;
        Changed();
    }

    public void NotifySelectedPropertiesChanged() => Changed();

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        DrawHostBackground(canvas);
        foreach (TouchControlDefinition control in Profile.Controls
                     .Where(item => item.Visible || EditMode)
                     .OrderBy(item => item.ZIndex))
        {
            DrawControl(canvas, control);
        }
        if (!EditMode) DrawCrosshair(canvas);
    }

    private void DrawCrosshair(Canvas canvas)
    {
        float centerX = Width * 0.5f;
        float centerY = Height * 0.5f;
        float radius = Math.Clamp(Math.Min(Width, Height) * 0.012f, 7f, 15f);
        _stroke.Color = Color.Argb(220, 255, 255, 255);
        _stroke.StrokeWidth = 3f;
        _stroke.SetStyle(Paint.Style.Stroke);
        canvas.DrawLine(centerX - radius, centerY, centerX + radius, centerY, _stroke);
        canvas.DrawLine(centerX, centerY - radius, centerX, centerY + radius, _stroke);
    }

    private void DrawHostBackground(Canvas canvas)
    {
        if (!EditMode) return;
        _text.Color = Color.Rgb(92, 109, 132);
        _text.TextSize = Math.Max(18f, Height * 0.025f);
        canvas.DrawText("控制配置編輯器：選取並拖動按鍵", Width * 0.5f, Height * 0.48f, _text);
    }

    private void DrawControl(Canvas canvas, TouchControlDefinition control)
    {
        RectF bounds = Bounds(control);
        float editOpacity = EditMode ? Math.Max(control.Opacity, 0.25f) : control.Opacity;
        bool down = control.Type == TouchControlType.Button &&
                    Input.IsDown(control.BindingKind, control.Binding);
        int alpha = (int)(Math.Clamp(editOpacity, 0f, 1f) * 255);
        _fill.Color = down
            ? Color.Argb(alpha, 62, 133, 180)
            : Color.Argb(alpha, 31, 39, 50);
        _fill.SetStyle(Paint.Style.Fill);
        _stroke.Color = control == _selected
            ? Color.Rgb(255, 201, 71)
            : Color.Argb(Math.Max(alpha, 118), 224, 231, 238);
        _stroke.SetStyle(Paint.Style.Stroke);
        _stroke.StrokeWidth = control == _selected ? 5f : 2.5f;

        if (control.Type == TouchControlType.Joystick)
        {
            float radius = Math.Min(bounds.Width(), bounds.Height()) * 0.48f;
            canvas.DrawCircle(bounds.CenterX(), bounds.CenterY(), radius, _fill);
            canvas.DrawCircle(bounds.CenterX(), bounds.CenterY(), radius, _stroke);
            float knobX = bounds.CenterX() + Input.MoveX * radius * 0.56f;
            float knobY = bounds.CenterY() - Input.MoveY * radius * 0.56f;
            _fill.Color = Color.Argb(Math.Max(alpha, 115), 104, 151, 181);
            canvas.DrawCircle(knobX, knobY, radius * 0.38f, _fill);
        }
        else if (control.Type == TouchControlType.DPad)
        {
            DrawDPad(canvas, bounds, alpha);
        }
        else
        {
            float corner = Math.Min(bounds.Width(), bounds.Height()) * 0.09f;
            canvas.DrawRoundRect(bounds, corner, corner, _fill);
            canvas.DrawRoundRect(bounds, corner, corner, _stroke);
        }

        if (!string.IsNullOrEmpty(control.Label))
        {
            _text.Color = Color.White;
            _text.TextSize = Math.Clamp(Math.Min(bounds.Width(), bounds.Height()) * 0.28f, 15f, 44f);
            float measuredWidth = _text.MeasureText(control.Label);
            float availableWidth = Math.Max(12f, bounds.Width() * 0.84f);
            if (measuredWidth > availableWidth)
                _text.TextSize *= availableWidth / measuredWidth;
            Paint.FontMetrics metrics = _text.GetFontMetrics()!;
            float baseline = bounds.CenterY() - (metrics.Ascent + metrics.Descent) * 0.5f;
            canvas.DrawText(control.Label, bounds.CenterX(), baseline, _text);
        }

        if (EditMode && control == _selected)
        {
            _fill.Color = Color.Rgb(255, 201, 71);
            canvas.DrawCircle(bounds.Right, bounds.Bottom, 13f, _fill);
        }
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null || Width <= 0 || Height <= 0) return false;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
            case MotionEventActions.PointerDown:
                BeginPointer(e, e.ActionIndex);
                break;
            case MotionEventActions.Move:
                for (int index = 0; index < e.PointerCount; index++) MovePointer(e, index);
                break;
            case MotionEventActions.Up:
            case MotionEventActions.PointerUp:
                EndPointer(e.GetPointerId(e.ActionIndex));
                break;
            case MotionEventActions.Cancel:
                CancelAllTouches();
                break;
        }
        Invalidate();
        return true;
    }

    private void BeginPointer(MotionEvent e, int index)
    {
        int pointerId = e.GetPointerId(index);
        float x = e.GetX(index);
        float y = e.GetY(index);
        TouchControlDefinition? control = HitTest(x, y);

        if (EditMode)
        {
            Select(control);
            if (control is null) return;
            RectF bounds = Bounds(control);
            bool resize = DistanceSquared(x, y, bounds.Right, bounds.Bottom) <= 34f * 34f;
            _captures[pointerId] = new PointerCapture
            {
                PointerId = pointerId,
                Kind = resize ? CaptureKind.EditResize : CaptureKind.EditDrag,
                Control = control,
                LastX = x,
                LastY = y,
            };
            return;
        }

        if (control is not null)
        {
            if (control.Type == TouchControlType.DPad && DPadCenter(control).Contains(x, y))
            {
                _captures[pointerId] = new PointerCapture
                {
                    PointerId = pointerId,
                    Kind = CaptureKind.Button,
                    Control = _dpadCrouchControl,
                    LastX = x,
                    LastY = y,
                };
                Input.Press(_dpadCrouchControl);
                return;
            }

            CaptureKind kind = control.Type is TouchControlType.Joystick or TouchControlType.DPad
                ? CaptureKind.Joystick
                : CaptureKind.Button;
            _captures[pointerId] = new PointerCapture
            {
                PointerId = pointerId, Kind = kind, Control = control, LastX = x, LastY = y,
            };
            if (kind == CaptureKind.Button) Input.Press(control);
            else UpdateMovementControl(control, x, y);
            return;
        }

        // Unoccupied right-side touches own camera look for their full lifetime.
        if (x >= Width * 0.32f)
        {
            _captures[pointerId] = new PointerCapture
            {
                PointerId = pointerId,
                Kind = CaptureKind.Look,
                StartX = x,
                StartY = y,
                LastX = x,
                LastY = y,
            };
            PostDelayed(() => BeginLookLongPress(pointerId), 360L);
        }
    }

    private void BeginLookLongPress(int pointerId)
    {
        if (!_captures.TryGetValue(pointerId, out PointerCapture? capture) ||
            capture.Kind != CaptureKind.Look || capture.LongPressCancelled || capture.LongPressTriggered)
            return;
        capture.LongPressTriggered = true;
        Input.Press(_lookBreakControl);
        PerformHapticFeedback(FeedbackConstants.LongPress);
        Invalidate();
    }

    private void MovePointer(MotionEvent e, int index)
    {
        int pointerId = e.GetPointerId(index);
        if (!_captures.TryGetValue(pointerId, out PointerCapture? capture)) return;
        float x = e.GetX(index);
        float y = e.GetY(index);
        switch (capture.Kind)
        {
            case CaptureKind.Joystick when capture.Control is not null:
                UpdateMovementControl(capture.Control, x, y);
                break;
            case CaptureKind.Look:
                if (!capture.LongPressTriggered &&
                    DistanceSquared(x, y, capture.StartX, capture.StartY) > 28f * 28f)
                    capture.LongPressCancelled = true;
                Input.AddLookDelta(
                    (x - capture.LastX) * Profile.HorizontalLookSensitivity,
                    (y - capture.LastY) * Profile.VerticalLookSensitivity * (Profile.InvertLookY ? -1f : 1f));
                break;
            case CaptureKind.EditDrag when capture.Control is not null:
                capture.Control.X = Math.Clamp(x / Width, 0.01f, 0.99f);
                capture.Control.Y = Math.Clamp(y / Height, 0.01f, 0.99f);
                ProfileChanged?.Invoke();
                break;
            case CaptureKind.EditResize when capture.Control is not null:
                float centerX = capture.Control.X * Width;
                float centerY = capture.Control.Y * Height;
                capture.Control.Width = Math.Clamp(MathF.Abs(x - centerX) * 2f / Width /
                                                   Math.Max(capture.Control.Scale, 0.01f), 0.025f, 0.7f);
                capture.Control.Height = Math.Clamp(MathF.Abs(y - centerY) * 2f / Height /
                                                    Math.Max(capture.Control.Scale, 0.01f), 0.025f, 0.7f);
                ProfileChanged?.Invoke();
                break;
        }
        capture.LastX = x;
        capture.LastY = y;
    }

    private void EndPointer(int pointerId)
    {
        if (!_captures.Remove(pointerId, out PointerCapture? capture)) return;
        if (capture.Kind == CaptureKind.Button && capture.Control is not null)
            Input.Release(capture.Control);
        if (capture.Kind == CaptureKind.Look && capture.LongPressTriggered)
            Input.Release(_lookBreakControl);
        if (capture.Kind == CaptureKind.Joystick)
            Input.SetMovement(0f, 0f);
        if (capture.Kind is CaptureKind.EditDrag or CaptureKind.EditResize)
            Changed();
    }

    private void CancelAllTouches()
    {
        foreach (PointerCapture capture in _captures.Values)
        {
            if (capture.Kind == CaptureKind.Button && capture.Control is not null)
                Input.Release(capture.Control);
            if (capture.Kind == CaptureKind.Look && capture.LongPressTriggered)
                Input.Release(_lookBreakControl);
        }
        _captures.Clear();
        Input.ReleaseAll();
        Invalidate();
    }

    private void UpdateMovementControl(TouchControlDefinition control, float x, float y)
    {
        RectF bounds = Bounds(control);
        float radius = Math.Max(1f, Math.Min(bounds.Width(), bounds.Height()) * 0.48f);
        float dx = (x - bounds.CenterX()) / radius;
        float dy = (bounds.CenterY() - y) / radius;
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 0.14f)
        {
            Input.SetMovement(0f, 0f);
            return;
        }
        if (control.Type == TouchControlType.DPad)
        {
            int sector = (int)MathF.Round(MathF.Atan2(dy, dx) / (MathF.PI / 4f));
            float angle = sector * MathF.PI / 4f;
            dx = MathF.Cos(angle);
            dy = MathF.Sin(angle);
        }
        else if (length > 1f)
        {
            (dx, dy) = (dx / length, dy / length);
        }
        Input.SetMovement(dx, dy);
    }

    private void DrawDPad(Canvas canvas, RectF bounds, int alpha)
    {
        string[,] arrows =
        {
            { "↖", "↑", "↗" },
            { "←", "", "→" },
            { "↙", "↓", "↘" },
        };
        float cellWidth = bounds.Width() / 3f;
        float cellHeight = bounds.Height() / 3f;
        _text.Color = Color.White;
        _text.TextSize = Math.Clamp(Math.Min(cellWidth, cellHeight) * 0.48f, 17f, 40f);
        Paint.FontMetrics metrics = _text.GetFontMetrics()!;
        for (int row = 0; row < 3; row++)
        for (int column = 0; column < 3; column++)
        {
            bool center = row == 1 && column == 1;
            var cell = new RectF(
                bounds.Left + column * cellWidth + 2f,
                bounds.Top + row * cellHeight + 2f,
                bounds.Left + (column + 1) * cellWidth - 2f,
                bounds.Top + (row + 1) * cellHeight - 2f);
            bool down = center && Input.IsDown(VirtualBindingKind.GameAction, "Crouch");
            _fill.Color = down
                ? Color.Argb(alpha, 62, 133, 180)
                : Color.Argb(alpha, 31, 39, 50);
            canvas.DrawRoundRect(cell, 5f, 5f, _fill);
            canvas.DrawRoundRect(cell, 5f, 5f, _stroke);
            float baseline = cell.CenterY() - (metrics.Ascent + metrics.Descent) * 0.5f;
            canvas.DrawText(center ? "SHF" : arrows[row, column], cell.CenterX(), baseline, _text);
        }
    }

    private RectF DPadCenter(TouchControlDefinition control)
    {
        RectF bounds = Bounds(control);
        float cellWidth = bounds.Width() / 3f;
        float cellHeight = bounds.Height() / 3f;
        return new RectF(
            bounds.Left + cellWidth,
            bounds.Top + cellHeight,
            bounds.Left + cellWidth * 2f,
            bounds.Top + cellHeight * 2f);
    }

    private TouchControlDefinition? HitTest(float x, float y) =>
        Profile.Controls
            .Where(control => (control.Visible || EditMode) && Bounds(control).Contains(x, y))
            .OrderByDescending(control => control.ZIndex)
            .FirstOrDefault();

    private RectF Bounds(TouchControlDefinition control)
    {
        float width = control.EffectiveWidth * Width;
        float height = control.EffectiveHeight * Height;
        float centerX = control.X * Width;
        float centerY = control.Y * Height;
        return new RectF(centerX - width * 0.5f, centerY - height * 0.5f,
            centerX + width * 0.5f, centerY + height * 0.5f);
    }

    private void Select(TouchControlDefinition? control)
    {
        if (ReferenceEquals(_selected, control)) return;
        _selected = control;
        SelectionChanged?.Invoke(control);
        Invalidate();
    }

    private void Changed()
    {
        ProfileChanged?.Invoke();
        Invalidate();
    }

    private static float DistanceSquared(float x1, float y1, float x2, float y2)
    {
        float dx = x1 - x2;
        float dy = y1 - y2;
        return dx * dx + dy * dy;
    }
}
