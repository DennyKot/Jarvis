using System.Drawing;
using System.Drawing.Imaging;
using Jarvis.Core.Tools;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Jarvis.Automation.Desktop.Screen;

public sealed record OcrLine(string Text, ScreenRect Bounds);

public sealed class OcrService
{
    public static IReadOnlyList<string> AvailableLanguages =>
        OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();

    public async Task<IReadOnlyList<OcrLine>> RecognizeAsync(Bitmap bitmap, ScreenRect region, string? language, CancellationToken cancellationToken)
    {
        var engine = CreateEngine(language)
            ?? throw new InvalidOperationException(
                $"No Windows OCR language pack for '{language ?? "user profile"}'. Installed: {string.Join(", ", AvailableLanguages)}.");

        // The engine rejects images larger than MaxImageDimension, so downscale and map coordinates back.
        var scale = Math.Max(1.0, Math.Max(bitmap.Width, bitmap.Height) / (double)OcrEngine.MaxImageDimension);
        using var source = scale > 1.0
            ? new Bitmap(bitmap, (int)(bitmap.Width / scale), (int)(bitmap.Height / scale))
            : (Bitmap)bitmap.Clone();

        using var softwareBitmap = await ToSoftwareBitmapAsync(source, cancellationToken);
        var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);

        return result.Lines
            .Select(line =>
            {
                var left = line.Words.Min(w => w.BoundingRect.X);
                var top = line.Words.Min(w => w.BoundingRect.Y);
                var right = line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width);
                var bottom = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
                return new OcrLine(line.Text, new ScreenRect(
                    region.X + left * scale, region.Y + top * scale, (right - left) * scale, (bottom - top) * scale));
            })
            .ToList();
    }

    private static OcrEngine? CreateEngine(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language == "auto")
        {
            return OcrEngine.TryCreateFromUserProfileLanguages();
        }

        var match = OcrEngine.AvailableRecognizerLanguages
            .FirstOrDefault(l => l.LanguageTag.StartsWith(language, StringComparison.OrdinalIgnoreCase));
        return match is null ? null : OcrEngine.TryCreateFromLanguage(new Language(match.LanguageTag));
    }

    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap bitmap, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        using var randomAccess = stream.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(randomAccess).AsTask(cancellationToken);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(cancellationToken);
    }
}
