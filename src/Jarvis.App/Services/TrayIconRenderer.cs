using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Jarvis.Core.Agent;

namespace Jarvis.App.Services;

/// <summary>Draws the tray icon: a small robot head whose eyes/antenna glow in a per-state accent colour.</summary>
internal static class TrayIconRenderer
{
    private const int Size = 64;
    private static readonly Dictionary<AssistantState, ImageSource> Cache = [];

    public static ImageSource Render(AssistantState state)
    {
        if (Cache.TryGetValue(state, out var cached))
        {
            return cached;
        }

        var accent = AccentFor(state);
        var accentBrush = Frozen(new SolidColorBrush(accent));
        var glowBrush = Frozen(new RadialGradientBrush(Color.FromArgb(0xC0, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B)));
        var shell = Frozen(new LinearGradientBrush(Color.FromRgb(0xE8, 0xEE, 0xFF), Color.FromRgb(0x9A, 0xA8, 0xD0), 90));
        var outline = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x16, 0x1A, 0x35))), 3));
        var visor = Frozen(new SolidColorBrush(Color.FromRgb(0x16, 0x1A, 0x35)));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Antenna
            dc.DrawLine(outline, new Point(32, 14), new Point(32, 5));
            dc.DrawEllipse(glowBrush, null, new Point(32, 6), 8, 8);
            dc.DrawEllipse(accentBrush, outline, new Point(32, 6), 4.5, 4.5);

            // Ears
            dc.DrawRoundedRectangle(shell, outline, new Rect(2, 28, 8, 18), 3, 3);
            dc.DrawRoundedRectangle(shell, outline, new Rect(54, 28, 8, 18), 3, 3);

            // Head + visor
            dc.DrawRoundedRectangle(shell, outline, new Rect(8, 14, 48, 46), 14, 14);
            dc.DrawRoundedRectangle(visor, null, new Rect(14, 24, 36, 22), 10, 10);

            // Eyes
            foreach (var x in new[] { 24.0, 40.0 })
            {
                dc.DrawEllipse(glowBrush, null, new Point(x, 35), 9, 9);
                dc.DrawEllipse(accentBrush, null, new Point(x, 35), 4.5, 5.5);
            }
        }

        var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        Cache[state] = bitmap;
        return bitmap;
    }

    public static string Describe(AssistantState state) => state switch
    {
        AssistantState.Idle => "Jarvis — ready (click to talk)",
        AssistantState.Error => "Jarvis — error (see log)",
        _ => $"Jarvis — {state.ToString().ToLowerInvariant()}…",
    };

    private static Color AccentFor(AssistantState state) => state switch
    {
        AssistantState.Listening => Color.FromRgb(0x3D, 0xFF, 0x8A),
        AssistantState.Transcribing or AssistantState.Thinking => Color.FromRgb(0xA0, 0x7C, 0xFF),
        AssistantState.Acting => Color.FromRgb(0xFF, 0xB1, 0x3D),
        AssistantState.Confirming => Color.FromRgb(0xFF, 0xE0, 0x3D),
        AssistantState.Speaking => Color.FromRgb(0x5A, 0xC8, 0xFF),
        AssistantState.Error => Color.FromRgb(0xFF, 0x4D, 0x5E),
        _ => Color.FromRgb(0x00, 0xE5, 0xFF),
    };

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
