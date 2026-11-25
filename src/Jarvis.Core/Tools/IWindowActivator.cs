namespace Jarvis.Core.Tools;

/// <summary>Brings a top-level window to the foreground so the user can see what Jarvis is doing.</summary>
public interface IWindowActivator
{
    bool Activate(nint windowHandle);

    /// <summary>Finds the top-level window of a process whose title contains <paramref name="titleFragment"/>.</summary>
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
