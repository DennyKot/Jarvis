using Jarvis.Core.Tools;

namespace Jarvis.Automation.Browser;

/// <summary>Viewport metrics reported by the page (CSS pixels).</summary>
public readonly record struct ViewportMetrics(double InnerWidth, double InnerHeight, double DevicePixelRatio);

/// <summary>Converts element boxes inside a Chrome tab into physical screen pixels for the AI pointer.</summary>
internal static class ViewportMapper
{
    /// <remarks>
    /// The page viewport is the bottom part of the Chrome window, horizontally centered between the thin side
    /// borders. Page zoom is ignored (assumed 100%), so CSS px * devicePixelRatio = physical px.
    /// </remarks>
    public static ScreenRect ToScreen(ScreenRect windowBounds, ViewportMetrics viewport, ScreenRect elementCss)
    {
        var scale = viewport.DevicePixelRatio <= 0 ? 1 : viewport.DevicePixelRatio;
        var viewportWidth = viewport.InnerWidth * scale;
        var viewportHeight = viewport.InnerHeight * scale;
        var border = Math.Max(0, (windowBounds.Width - viewportWidth) / 2);
        var viewportLeft = windowBounds.X + border;
        var viewportTop = windowBounds.Y + windowBounds.Height - border - viewportHeight;

        return new ScreenRect(
            viewportLeft + elementCss.X * scale,
            viewportTop + elementCss.Y * scale,
            elementCss.Width * scale,
            elementCss.Height * scale);
    }
}
