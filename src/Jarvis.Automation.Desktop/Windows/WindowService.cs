using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Jarvis.Automation.Desktop.Input;
using Jarvis.Automation.Desktop.Interop;
using Jarvis.Core.Tools;

namespace Jarvis.Automation.Desktop.Windows;

public sealed record WindowInfo(nint Handle, string Title, string ProcessName, int ProcessId, ScreenRect Bounds, bool IsMinimized)
{
    public override string ToString() =>
        $"\"{Title}\" — {ProcessName}{(IsMinimized ? " (minimized)" : string.Empty)} {Bounds}";
}

public sealed class WindowService(InputSimulator input) : IWindowActivator
{
    private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "TextInputHost", "ShellExperienceHost", "SearchHost", "StartMenuExperienceHost", "LockApp", "Jarvis",
    };

    public IReadOnlyList<WindowInfo> ListWindows()
    {
        var windows = new List<WindowInfo>();
        foreach (var handle in EnumerateTopLevelWindows())
        {
            if (!NativeMethods.IsWindowVisible(handle) || NativeMethods.IsCloaked(handle) ||
                NativeMethods.GetWindow(handle, NativeMethods.GW_OWNER) != 0 ||
                (NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE) & (nint)NativeMethods.WS_EX_TOOLWINDOW) != 0)
            {
                continue;
            }

            var title = NativeMethods.GetWindowTitle(handle);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            NativeMethods.GetWindowThreadProcessId(handle, out var pid);
            var processName = TryGetProcessName((int)pid);
            if (IgnoredProcesses.Contains(processName) || (processName == "explorer" && title == "Program Manager"))
            {
                continue;
            }

            var rect = NativeMethods.GetWindowBounds(handle);
            windows.Add(new WindowInfo(handle, title, processName, (int)pid,
                new ScreenRect(rect.Left, rect.Top, rect.Width, rect.Height), NativeMethods.IsIconic(handle)));
        }

        return windows;
    }

    /// <summary>Finds the best window whose title or process name matches <paramref name="query"/>.</summary>
    public WindowInfo? FindWindow(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var windows = ListWindows();
        return windows.FirstOrDefault(w => w.Title.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? windows.FirstOrDefault(w => w.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            ?? windows.FirstOrDefault(w => w.ProcessName.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? windows.FirstOrDefault(w => w.ProcessName.Contains(query.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase));
    }

    public nint FindWindow(string processName, string? titleFragment) =>
        ListWindows()
            .Where(w => w.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(w => titleFragment is not null && w.Title.Contains(titleFragment, StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Handle)
            .FirstOrDefault();

    public WindowInfo? GetForegroundWindow()
    {
        var handle = NativeMethods.GetForegroundWindow();
        return ListWindows().FirstOrDefault(w => w.Handle == handle);
    }

    public bool Activate(nint windowHandle)
    {
        if (windowHandle == 0 || !NativeMethods.IsWindow(windowHandle))
        {
            return false;
        }

        if (NativeMethods.IsIconic(windowHandle))
        {
            NativeMethods.ShowWindow(windowHandle, NativeMethods.SW_RESTORE);
        }

        if (NativeMethods.GetForegroundWindow() == windowHandle)
        {
            return true;
        }

        // Windows only lets the foreground process change the foreground window. Attaching to the
        // current foreground thread's input (and tapping Alt) is the documented-in-practice workaround.
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
        var currentThread = NativeMethods.GetCurrentThreadId();
        var attached = foregroundThread != currentThread &&
                       NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            input.SendKeys("alt");
            NativeMethods.BringWindowToTop(windowHandle);
            NativeMethods.SetForegroundWindow(windowHandle);
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        Thread.Sleep(150);
        return NativeMethods.GetForegroundWindow() == windowHandle;
    }

    public HashSet<nint> SnapshotHandles() => ListWindows().Select(w => w.Handle).ToHashSet();

    public ScreenRect? GetWindowBounds(nint windowHandle)
    {
        if (windowHandle == 0 || !NativeMethods.IsWindow(windowHandle))
        {
            return null;
        }

        var rect = NativeMethods.GetWindowBounds(windowHandle);
        return new ScreenRect(rect.Left, rect.Top, rect.Width, rect.Height);
    }

    private static unsafe List<nint> EnumerateTopLevelWindows()
    {
        var handles = new List<nint>();
        var gcHandle = GCHandle.Alloc(handles);
        try
        {
            NativeMethods.EnumWindows(&EnumWindowsCallback, GCHandle.ToIntPtr(gcHandle));
        }
        finally
        {
            gcHandle.Free();
        }

        return handles;
    }

    [UnmanagedCallersOnly]
    private static int EnumWindowsCallback(nint hWnd, nint lParam)
    {
        if (GCHandle.FromIntPtr(lParam).Target is List<nint> handles)
        {
            handles.Add(hWnd);
        }

        return 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string TryGetProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }
}
