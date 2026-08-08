using System.Diagnostics;

namespace RTXSandbox.Android.TouchControls;

internal static class TouchControlSelfTest
{
    [Conditional("DEBUG")]
    public static void Run()
    {
        TouchControlProfile defaults = TouchControlProfile.CreateDefault();
        Require(defaults.Controls.Count == 8, "default control count");
        Require(defaults.Controls.Single(control => control.Id == "jump").Binding == "Space", "jump binding");
        Require(defaults.Controls.Single(control => control.Id == "inventory").Label == "E", "inventory label");
        Require(defaults.Controls.Single(control => control.Id == "movement").Type == TouchControlType.DPad,
            "eight-direction movement default");
        Require(defaults.Controls.Single(control => control.Id == "back").Binding == "Escape", "back binding");

        var input = new VirtualInputState();
        TouchControlDefinition left = defaults.Controls.Single(control => control.Id == "mouse-left");
        TouchControlDefinition duplicate = left.Clone();
        input.Press(left);
        input.Press(duplicate);
        Require(input.ConsumePressed(VirtualBindingKind.Mouse, "Left"), "short press edge retained");
        Require(!input.ConsumePressed(VirtualBindingKind.Mouse, "Left"), "press edge consumed once");
        input.Release(left);
        Require(input.IsDown(VirtualBindingKind.Mouse, "Left"), "multi-finger reference count");
        input.Release(duplicate);
        Require(!input.IsDown(VirtualBindingKind.Mouse, "Left"), "multi-finger release");

        var toggle = new TouchControlDefinition
        {
            BindingKind = VirtualBindingKind.Keyboard,
            Binding = "Shift",
            Mode = TouchButtonMode.Toggle,
        };
        input.Press(toggle);
        input.Release(toggle);
        Require(input.IsDown(VirtualBindingKind.Keyboard, "Shift"), "toggle enable");
        input.Press(toggle);
        Require(!input.IsDown(VirtualBindingKind.Keyboard, "Shift"), "toggle disable");

        input.SetMovement(0.7f, 0.7f);
        Require(input.MoveX > 0.69f && input.MoveY > 0.69f, "diagonal movement");
        input.SetHardwareMovement(-0.2f, 0.2f);
        (float combinedX, float combinedY) = input.GetMovement();
        Require(combinedX > 0.49f && combinedY > 0.89f, "touch and hardware movement merge");
        input.SetHardwareMovement(0f, 0f);
        var crouch = new TouchControlDefinition
        {
            BindingKind = VirtualBindingKind.GameAction,
            Binding = "Crouch",
        };
        input.Press(crouch);
        Require(input.IsDown(VirtualBindingKind.GameAction, "Crouch"), "D-pad center crouch");
        input.Release(crouch);
        input.AddLookDelta(3f, -2f);
        (float x, float y) = input.ConsumeLookDelta();
        Require(x == 3f && y == -2f && input.LookDeltaX == 0f, "look delta consumption");

        input.BeginHardwareRebind(VirtualBindingKind.Keyboard, "W");
        Require(input.TryCaptureHardwareKey("Q"), "hardware key capture");
        Require(input.TryResolveHardwareKey("Q", out VirtualBindingKind reboundKind, out string reboundBinding) &&
                reboundKind == VirtualBindingKind.Keyboard && reboundBinding == "W", "hardware key resolve");
        Require(input.IsHardwareBindingRemapped(VirtualBindingKind.Keyboard, "W"), "default key replaced");
        input.ResetHardwareBindings();
        Require(!input.IsHardwareBindingRemapped(VirtualBindingKind.Keyboard, "W"), "hardware reset");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException($"Touch control self-test failed: {name}.");
    }
}
