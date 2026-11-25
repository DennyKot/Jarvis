using System.ComponentModel;
using System.Runtime.InteropServices;
using Jarvis.Automation.Desktop.Interop;
using static Jarvis.Automation.Desktop.Interop.InputConstants;

namespace Jarvis.Automation.Desktop.Input;

public enum MouseButton
{
    Left,
    Right,
}

/// <summary>Synthesizes real keyboard and mouse input with SendInput.</summary>
public sealed class InputSimulator
{
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();
    private static readonly HashSet<ushort> ExtendedKeys =
        [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5D];

    public void SendKeys(string keys)
    {
        foreach (var chord in KeyChordParser.ParseSequence(keys))
        {
            var inputs = new List<INPUT>();
            inputs.AddRange(chord.Modifiers.Select(m => Key(m, up: false)));
            inputs.Add(Key(chord.Key, up: false));
            inputs.Add(Key(chord.Key, up: true));
            inputs.AddRange(chord.Modifiers.Reverse().Select(m => Key(m, up: true)));
            Send(inputs);
            Thread.Sleep(40);
        }
    }

    public void TypeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // One SendInput per character: WinUI apps (Windows 11 Notepad, Terminal) resolve VK_PACKET characters
        // lazily, so batching many unicode events makes them all come out as the last character of the batch.
        foreach (var c in text.Replace("\r\n", "\n"))
        {
            if (c == '\n')
            {
                Send([Key(0x0D, up: false), Key(0x0D, up: true)]);
            }
            else
            {
                Send([Unicode(c, up: false), Unicode(c, up: true)]);
            }

            Thread.Sleep(3);
        }
    }

    /// <summary>Clicks with the real mouse and puts the user's cursor back where it was.</summary>
    public void Click(int x, int y, MouseButton button = MouseButton.Left, bool doubleClick = false)
    {
        NativeMethods.GetCursorPos(out var original);
        try
        {
            if (!NativeMethods.SetCursorPos(x, y))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            Thread.Sleep(30);
            var (down, up) = button == MouseButton.Right
                ? (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP)
                : (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP);

            for (var i = 0; i < (doubleClick ? 2 : 1); i++)
            {
                Send([Mouse(down), Mouse(up)]);
                Thread.Sleep(60);
            }
        }
        finally
        {
            Thread.Sleep(50);
            NativeMethods.SetCursorPos(original.X, original.Y);
        }
    }

    public void Scroll(int x, int y, int notches)
    {
        NativeMethods.GetCursorPos(out var original);
        try
        {
            NativeMethods.SetCursorPos(x, y);
            Thread.Sleep(30);
            var wheel = Mouse(MOUSEEVENTF_WHEEL);
            wheel.Data.Mouse.MouseData = unchecked((uint)(notches * 120));
            Send([wheel]);
        }
        finally
        {
            Thread.Sleep(50);
            NativeMethods.SetCursorPos(original.X, original.Y);
        }
    }

    private static void Send(IReadOnlyCollection<INPUT> inputs)
    {
        var array = inputs.ToArray();
        var sent = NativeMethods.SendInput((uint)array.Length, array, InputSize);
        if (sent != array.Length)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(),
                "SendInput was blocked. The target window may run as administrator, or the session is locked.");
        }
    }

    private static INPUT Key(ushort virtualKey, bool up) => new()
    {
        Type = INPUT_KEYBOARD,
        Data = new InputUnion
        {
            Keyboard = new KEYBDINPUT
            {
                VirtualKey = virtualKey,
                Flags = (up ? KEYEVENTF_KEYUP : 0) | (ExtendedKeys.Contains(virtualKey) ? KEYEVENTF_EXTENDEDKEY : 0),
            },
        },
    };

    private static INPUT Unicode(char c, bool up) => new()
    {
        Type = INPUT_KEYBOARD,
        Data = new InputUnion
        {
            Keyboard = new KEYBDINPUT { ScanCode = c, Flags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) },
        },
    };

    private static INPUT Mouse(uint flags) => new()
    {
        Type = INPUT_MOUSE,
        Data = new InputUnion { Mouse = new MOUSEINPUT { Flags = flags } },
    };
}
