using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Jarvis.App.Controls;
using Jarvis.Core.Agent;
using DrawingIcon = System.Drawing.Icon;

namespace Jarvis.App.Services;

internal static class TrayIconRenderer
{
    private const int Size = 64;
    private static readonly Dictionary<AssistantState, byte[]> Cache = [];

    /// <summary>
    /// Creates the icon for TaskbarIcon.Icon. H.NotifyIcon's IconSource only accepts file/resource-backed images
    /// (it throws NotImplementedException for a RenderTargetBitmap), so the drawing is encoded to ICO bytes once per
    /// state. A new Icon is returned on every call because TaskbarIcon disposes the previous one when it changes.
    /// </summary>
    public static DrawingIcon Render(AssistantState state)
    {
        if (!Cache.TryGetValue(state, out var bytes))
        {
            using var ico = H.NotifyIcon.ImageExtensions.ToStream(Draw(state));
            using var buffer = new MemoryStream();
            ico.CopyTo(buffer);
            bytes = buffer.ToArray();
            Cache[state] = bytes;
        }

        return new DrawingIcon(new MemoryStream(bytes));
    }

    private static BitmapSource Draw(AssistantState state)
    {
        // Big, bold shapes that fill the canvas: the tray shows this at 16-32 px, so thin details just turn to mush.
        var accent = JarvisCharacter.StateColor(state);
        var accentBrush = Frozen(new SolidColorBrush(accent));
        var glowBrush = Frozen(new RadialGradientBrush(Color.FromArgb(0xB0, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B)));
        var shell = Frozen(new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xA9, 0xB6, 0xF0), 90));
        var dark = Frozen(new SolidColorBrush(Color.FromRgb(0x16, 0x1A, 0x35)));
        var outline = Frozen(new Pen(dark, 2.5));
        var visor = Frozen(new LinearGradientBrush(Color.FromRgb(0x23, 0x2A, 0x55), Color.FromRgb(0x10, 0x13, 0x2B), 90));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Antenna
            dc.DrawLine(Frozen(new Pen(dark, 4)), new Point(32, 14), new Point(32, 8));
            dc.DrawEllipse(accentBrush, outline, new Point(32, 7), 5.5, 5.5);

            // Head + face screen
            dc.DrawRoundedRectangle(shell, outline, new Rect(2.5, 13, 59, 49), 19, 19);
            dc.DrawRoundedRectangle(visor, null, new Rect(8, 21, 48, 33), 14, 14);

            // Eyes (crosses on error, like the avatar)
            foreach (var x in new[] { 21.5, 42.5 })
            {
                dc.DrawEllipse(glowBrush, null, new Point(x, 37.5), 13, 13);
                if (state == AssistantState.Error)
                {
                    var cross = Frozen(new Pen(accentBrush, 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
                    dc.DrawLine(cross, new Point(x - 5.5, 31), new Point(x + 5.5, 44));
                    dc.DrawLine(cross, new Point(x + 5.5, 31), new Point(x - 5.5, 44));
                }
                else
                {
                    dc.DrawRoundedRectangle(accentBrush, null, new Rect(x - 6, 28.5, 12, 18), 6, 6);
                    dc.DrawEllipse(Brushes.White, null, new Point(x - 2, 33), 2, 2);
                }
            }
        }

        var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static string Describe(AssistantState state) => state switch
    {
        AssistantState.Idle => "Jarvis — ready (click to talk)",
        AssistantState.Error => "Jarvis — error (see log)",
        _ => $"Jarvis — {state.ToString().ToLowerInvariant()}…",
    };

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
