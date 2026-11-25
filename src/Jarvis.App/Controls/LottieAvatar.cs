using System.IO;
using System.Windows.Media;
using Jarvis.Core.Agent;
using SkiaSharp;
using SkiaSharp.Skottie;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace Jarvis.App.Controls;

/// <summary>
/// Plays designer-made Lottie clips from Assets\Avatar\{state}.json (idle.json, listening.json, thinking.json, ...).
/// When no clip exists for a state, <see cref="HasClip"/> is false and the built-in character is shown instead.
/// </summary>
public sealed class LottieAvatar : SKElement
{
    private readonly Dictionary<AssistantState, Animation?> _clips = [];
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Avatar");
    private Animation? _current;
    private DateTime _clipStart = DateTime.UtcNow;

    public LottieAvatar()
    {
        CompositionTarget.Rendering += (_, _) =>
        {
            if (_current is not null && IsVisible)
            {
                InvalidateVisual();
            }
        };
    }

    public bool HasClip(AssistantState state) => Load(state) is not null;

    public void Play(AssistantState state)
    {
        var clip = Load(state) ?? Load(AssistantState.Idle);
        if (!ReferenceEquals(clip, _current))
        {
            _current = clip;
            _clipStart = DateTime.UtcNow;
        }
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        base.OnPaintSurface(e);
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        if (_current is null)
        {
            return;
        }

        var duration = _current.Duration.TotalSeconds;
        var time = duration > 0 ? (DateTime.UtcNow - _clipStart).TotalSeconds % duration : 0;
        _current.SeekFrameTime(time);
        _current.Render(canvas, new SKRect(0, 0, e.Info.Width, e.Info.Height));
    }

    private Animation? Load(AssistantState state)
    {
        if (_clips.TryGetValue(state, out var cached))
        {
            return cached;
        }

        var path = Path.Combine(_directory, state.ToString().ToLowerInvariant() + ".json");
        Animation? animation = null;
        if (File.Exists(path) && Animation.TryCreate(path, out var created))
        {
            animation = created;
        }

        _clips[state] = animation;
        return animation;
    }
}
