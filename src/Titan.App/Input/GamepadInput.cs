using Windows.Gaming.Input;

namespace Titan.App.Input;

public enum PadButton { A, B, X, Y, LB, RB, Menu, View }
public enum PadDirection { Up, Down, Left, Right }

/// <summary>
/// Polls the first Xbox controller (an Elite Series 2 shows up as a standard Gamepad; its paddles can be
/// mapped to A/B/X/Y in the Xbox Accessories app). Edge-detects buttons, repeats navigation like a keyboard,
/// exposes the analog triggers and drives the impulse triggers.
/// </summary>
public sealed class GamepadInput
{
    private GamepadReading _prev;
    private double _repeatAt;
    private bool _held;
    private float _surge;
    private double _lastRumble;

    public bool Connected => Gamepad.Gamepads.Count > 0;
    public float LeftTrigger { get; private set; }
    public float RightTrigger { get; private set; }
    /// <summary>Right stick, y up. Zero inside the dead zone.</summary>
    public (float X, float Y) RightStick { get; private set; }

    public event Action<PadButton>? Pressed;
    public event Action<PadDirection>? Navigate;
    /// <summary>LT and RT held fully for 0.6 s.</summary>
    public event Action? Discharge;
    public event Action<bool>? ConnectionChanged;

    public GamepadInput()
    {
        Gamepad.GamepadAdded += (_, _) => ConnectionChanged?.Invoke(true);
        Gamepad.GamepadRemoved += (_, _) => ConnectionChanged?.Invoke(Connected);
    }

    public void Poll(double nowMs, float dt)
    {
        var pads = Gamepad.Gamepads;
        if (pads.Count == 0) { LeftTrigger = RightTrigger = 0; RightStick = (0, 0); return; }
        var pad = pads[0];
        var r = pad.GetCurrentReading();

        LeftTrigger = (float)r.LeftTrigger;
        RightTrigger = (float)r.RightTrigger;
        float rx = (float)r.RightThumbstickX, ry = (float)r.RightThumbstickY;
        RightStick = MathF.Sqrt(rx * rx + ry * ry) > .18f ? (rx, ry) : (0, 0);

        bool Down(GamepadButtons b) => (r.Buttons & b) != 0;
        bool Edge(GamepadButtons b) => Down(b) && (_prev.Buttons & b) == 0;
        if (Edge(GamepadButtons.A)) Pressed?.Invoke(PadButton.A);
        if (Edge(GamepadButtons.B)) Pressed?.Invoke(PadButton.B);
        if (Edge(GamepadButtons.X)) Pressed?.Invoke(PadButton.X);
        if (Edge(GamepadButtons.Y)) Pressed?.Invoke(PadButton.Y);
        if (Edge(GamepadButtons.LeftShoulder)) Pressed?.Invoke(PadButton.LB);
        if (Edge(GamepadButtons.RightShoulder)) Pressed?.Invoke(PadButton.RB);
        if (Edge(GamepadButtons.Menu)) Pressed?.Invoke(PadButton.Menu);
        if (Edge(GamepadButtons.View)) Pressed?.Invoke(PadButton.View);

        // Left stick / d-pad move focus, with a keyboard-like repeat.
        PadDirection? dir = null;
        if (Down(GamepadButtons.DPadUp)) dir = PadDirection.Up;
        else if (Down(GamepadButtons.DPadDown)) dir = PadDirection.Down;
        else if (Down(GamepadButtons.DPadLeft)) dir = PadDirection.Left;
        else if (Down(GamepadButtons.DPadRight)) dir = PadDirection.Right;
        else
        {
            double lx = r.LeftThumbstickX, ly = r.LeftThumbstickY;
            if (Math.Sqrt(lx * lx + ly * ly) > .5)
                dir = Math.Abs(lx) > Math.Abs(ly) ? (lx > 0 ? PadDirection.Right : PadDirection.Left)
                                                  : (ly > 0 ? PadDirection.Up : PadDirection.Down);
        }
        if (dir is PadDirection d)
        {
            if (nowMs >= _repeatAt) { Navigate?.Invoke(d); _repeatAt = nowMs + (_held ? 120 : 280); _held = true; }
        }
        else { _held = false; _repeatAt = 0; }

        // LT + RT fully held → discharge (with a cooldown).
        if (LeftTrigger > .95f && RightTrigger > .95f)
        {
            if (_surge >= 0) { _surge += dt; if (_surge > .6f) { _surge = -1.5f; Kick(pad); Discharge?.Invoke(); } }
        }
        else if (_surge > 0) _surge = 0;
        if (_surge < 0) _surge = MathF.Min(0, _surge + dt);

        // Impulse triggers follow how hard each trigger is pressed.
        if (_surge >= 0 && nowMs - _lastRumble > 60)
        {
            _lastRumble = nowMs;
            pad.Vibration = new GamepadVibration
            {
                LeftTrigger = LeftTrigger * .35,
                RightTrigger = RightTrigger * .5,
                LeftMotor = 0,
                RightMotor = 0,
            };
        }
        _prev = r;
    }

    private static void Kick(Gamepad pad)
    {
        pad.Vibration = new GamepadVibration { LeftMotor = .8, RightMotor = .5, LeftTrigger = 1, RightTrigger = 1 };
        _ = Task.Delay(380).ContinueWith(_ => pad.Vibration = default);
    }
}
