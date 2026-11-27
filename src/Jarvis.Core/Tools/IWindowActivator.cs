namespace Jarvis.Core.Tools;

public interface IWindowActivator
{
    bool Activate(nint windowHandle);

    nint FindWindow(string processName, string? titleFragment);

    /// <summary>Visible window bounds in physical screen pixels, or null when the window is gone.</summary>
    ScreenRect? GetWindowBounds(nint windowHandle);
}

public sealed class NullWindowActivator : IWindowActivator
{
    public bool Activate(nint windowHandle) => false;

    public nint FindWindow(string processName, string? titleFragment) => 0;

    public ScreenRect? GetWindowBounds(nint windowHandle) => null;
}
