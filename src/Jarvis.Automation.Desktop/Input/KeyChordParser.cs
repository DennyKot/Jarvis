using Jarvis.Automation.Desktop.Interop;

namespace Jarvis.Automation.Desktop.Input;

/// <summary>One key combination such as "ctrl+shift+s": modifiers are held while the main key is pressed.</summary>
internal sealed record KeyChord(IReadOnlyList<ushort> Modifiers, ushort Key);

internal static class KeyChordParser
{
    private static readonly Dictionary<string, ushort> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11,
        ["shift"] = 0x10,
        ["alt"] = 0x12,
        ["win"] = 0x5B, ["windows"] = 0x5B, ["meta"] = 0x5B, ["cmd"] = 0x5B,
    };

    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 0x0D, ["return"] = 0x0D,
        ["esc"] = 0x1B, ["escape"] = 0x1B,
        ["tab"] = 0x09,
        ["space"] = 0x20, ["spacebar"] = 0x20,
        ["backspace"] = 0x08, ["back"] = 0x08,
        ["delete"] = 0x2E, ["del"] = 0x2E,
        ["insert"] = 0x2D, ["ins"] = 0x2D,
        ["home"] = 0x24, ["end"] = 0x23,
        ["pageup"] = 0x21, ["pgup"] = 0x21,
        ["pagedown"] = 0x22, ["pgdn"] = 0x22,
        ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["capslock"] = 0x14,
        ["printscreen"] = 0x2C,
        ["menu"] = 0x5D, ["apps"] = 0x5D,
        ["plus"] = 0xBB, ["minus"] = 0xBD, ["comma"] = 0xBC, ["period"] = 0xBE,
    };

    /// <summary>Parses a space-separated sequence of chords, e.g. "ctrl+k ctrl+o" or "alt+f4".</summary>
    public static IReadOnlyList<KeyChord> ParseSequence(string keys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keys);
        return keys.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Parse)
            .ToList();
    }

    public static KeyChord Parse(string chord)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chord);

        // "ctrl++" means Ctrl and the plus key.
        var normalized = chord.EndsWith("++", StringComparison.Ordinal) ? chord[..^2] + "+plus" : chord;
        var parts = normalized.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new FormatException($"Invalid key combination '{chord}'.");
        }

        var modifiers = new List<ushort>();
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!Modifiers.TryGetValue(parts[i], out var modifier))
            {
                throw new FormatException($"'{parts[i]}' is not a modifier key in '{chord}'.");
            }

            modifiers.Add(modifier);
        }

        var last = parts[^1];
        if (modifiers.Count == 0 && Modifiers.TryGetValue(last, out var lone))
        {
            return new KeyChord([], lone);
        }

        return new KeyChord(modifiers, ParseKey(last, chord));
    }

    private static ushort ParseKey(string key, string chord)
    {
        if (NamedKeys.TryGetValue(key, out var named))
        {
            return named;
        }

        if (key.Length > 1 && (key[0] is 'f' or 'F') && int.TryParse(key[1..], out var function) && function is >= 1 and <= 24)
        {
            return (ushort)(0x70 + function - 1);
        }

        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return c;
            }

            var scan = NativeMethods.VkKeyScan(key[0]);
            if (scan != -1)
            {
                return (ushort)(scan & 0xFF);
            }
        }

        throw new FormatException($"Unknown key '{key}' in '{chord}'.");
    }
}
