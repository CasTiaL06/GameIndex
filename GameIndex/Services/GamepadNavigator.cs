using Microsoft.UI.Dispatching;
using Windows.Gaming.Input;

namespace GameIndex.Services;

public enum PadAction { Left, Right, Up, Down, Accept, Back, Favorite, PageLeft, PageRight }

/// <summary>Polls the first connected gamepad (D-pad / left stick / A / B / Y) and raises navigation events. Windows only delivers input while the window is focused.</summary>
public sealed class GamepadNavigator : IDisposable
{
    private readonly DispatcherQueueTimer _timer;
    private GamepadButtons _prev;
    private DateTime _nextRepeat = DateTime.MinValue;
    private PadAction? _held;

    public event Action<PadAction>? Action;

    public GamepadNavigator(DispatcherQueue queue)
    {
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(40);
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    private void Poll()
    {
        var pad = Gamepad.Gamepads.FirstOrDefault();
        if (pad is null) return;
        var r = pad.GetCurrentReading();
        var pressed = r.Buttons & ~_prev;
        _prev = r.Buttons;

        if (pressed.HasFlag(GamepadButtons.A)) Action?.Invoke(PadAction.Accept);
        if (pressed.HasFlag(GamepadButtons.B)) Action?.Invoke(PadAction.Back);
        if (pressed.HasFlag(GamepadButtons.Y)) Action?.Invoke(PadAction.Favorite);
        if (pressed.HasFlag(GamepadButtons.LeftShoulder)) Action?.Invoke(PadAction.PageLeft);     // LB / RB: jump 10 games
        if (pressed.HasFlag(GamepadButtons.RightShoulder)) Action?.Invoke(PadAction.PageRight);

        PadAction? dir =
            r.Buttons.HasFlag(GamepadButtons.DPadLeft) || r.LeftThumbstickX < -0.6 ? PadAction.Left :
            r.Buttons.HasFlag(GamepadButtons.DPadRight) || r.LeftThumbstickX > 0.6 ? PadAction.Right :
            r.Buttons.HasFlag(GamepadButtons.DPadUp) || r.LeftThumbstickY > 0.6 ? PadAction.Up :
            r.Buttons.HasFlag(GamepadButtons.DPadDown) || r.LeftThumbstickY < -0.6 ? PadAction.Down : null;

        if (dir is null) { _held = null; return; }
        var now = DateTime.UtcNow;
        if (_held != dir) { _held = dir; _nextRepeat = now.AddMilliseconds(380); Action?.Invoke(dir.Value); }   // first press, then auto-repeat
        else if (now >= _nextRepeat) { _nextRepeat = now.AddMilliseconds(170); Action?.Invoke(dir.Value); }
    }

    public void Dispose() => _timer.Stop();
}
