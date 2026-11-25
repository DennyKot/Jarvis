using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Jarvis.Automation.Desktop.Interop;
using Jarvis.Core.Tools;

namespace Jarvis.Automation.Desktop.Screen;

/// <summary>How pixels of the last screenshot sent to the model map back to physical screen pixels.</summary>
public readonly record struct ScreenshotMapping(ScreenRect Region, double Scale, int ImageWidth, int ImageHeight)
{
    public (int X, int Y) ToScreen(double imageX, double imageY) =>
        ((int)Math.Round(Region.X + imageX * Scale), (int)Math.Round(Region.Y + imageY * Scale));
}

public sealed record CapturedImage(byte[] Data, string MediaType, ScreenshotMapping Mapping);

public sealed class ScreenCaptureService
{
    private const int MaxLongSide = 1280;

    public ScreenshotMapping? LastMapping { get; private set; }

    public static ScreenRect VirtualScreen => new(
        NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));

    public Bitmap CaptureBitmap(ScreenRect region)
    {
        var clipped = Clip(region);
        var bitmap = new Bitmap((int)clipped.Width, (int)clipped.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen((int)clipped.X, (int)clipped.Y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    /// <summary>Captures a region, downscales it for the vision model and encodes it as JPEG.</summary>
    public CapturedImage CaptureForModel(ScreenRect region)
    {
        var clipped = Clip(region);
        using var full = CaptureBitmap(clipped);
        var scale = Math.Max(1.0, Math.Max(full.Width, full.Height) / (double)MaxLongSide);
        var width = (int)Math.Round(full.Width / scale);
        var height = (int)Math.Round(full.Height / scale);

        using var resized = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(resized))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(full, 0, 0, width, height);
        }

        using var stream = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, 70L);
        resized.Save(stream, encoder, parameters);

        var mapping = new ScreenshotMapping(clipped, scale, width, height);
        LastMapping = mapping;
        return new CapturedImage(stream.ToArray(), "image/jpeg", mapping);
    }

    private static ScreenRect Clip(ScreenRect region)
    {
        var screen = VirtualScreen;
        if (region.IsEmpty)
        {
            return screen;
        }

        var left = Math.Max(region.X, screen.X);
        var top = Math.Max(region.Y, screen.Y);
        var right = Math.Min(region.X + region.Width, screen.X + screen.Width);
        var bottom = Math.Min(region.Y + region.Height, screen.Y + screen.Height);
        if (right <= left || bottom <= top)
        {
            throw new InvalidOperationException("The requested region is outside of the screen.");
        }

        return new ScreenRect(left, top, right - left, bottom - top);
    }
}
