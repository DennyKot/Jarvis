using System.Windows.Input;
using System.Windows.Interop;
using Jarvis.App.Interop;
using Microsoft.Extensions.Logging;

namespace Jarvis.App.Services;

public sealed class GlobalHotkeys : IDisposable
{
    private readonly ILogger<GlobalHotkeys> _logger;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = [];
    private int _nextId = 1;

    public GlobalHotkeys(ILogger<GlobalHotkeys> logger)
    {
        _logger = logger;
        _source = new HwndSource(new HwndSourceParameters("JarvisHotkeys") { ParentWindow = new nint(-3) });
        _source.AddHook(WndProc);
    }

    /// <returns>False if the combination is invalid or already taken by another app.</returns>
    public bool Register(string gesture, Action handler)
    {
        if (!TryParse(gesture, out var modifiers, out var key))
        {
            _logger.LogWarning("Invalid hotkey '{Gesture}'", gesture);
            return false;
        }

        var id = _nextId++;
        if (!Win32.RegisterHotKey(_source.Handle, id, modifiers | Win32.MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(key)))
        {
            _logger.LogWarning("Hotkey '{Gesture}' is already used by another application", gesture);
            return false;
        }

        _handlers[id] = handler;
        return true;
    }

    internal static bool TryParse(string gesture, out uint modifiers, out Key key)
    {
        modifiers = 0;
        key = Key.None;
        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts[..^1])
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= Win32.MOD_CONTROL; break;
                case "alt": modifiers |= Win32.MOD_ALT; break;
                case "shift": modifiers |= Win32.MOD_SHIFT; break;
                case "win": modifiers |= Win32.MOD_WIN; break;
                default: return false;
            }
        }

        return parts.Length > 0 && Enum.TryParse(parts[^1], ignoreCase: true, out key) && key != Key.None;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && _handlers.TryGetValue((int)wParam, out var handler))
        {
            handled = true;
            handler();
        }

        return 0;
    }

    public void Dispose()
    {
        foreach (var id in _handlers.Keys)
        {
            Win32.UnregisterHotKey(_source.Handle, id);
        }

        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
