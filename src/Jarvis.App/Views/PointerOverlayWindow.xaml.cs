using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Jarvis.App.Interop;
using Jarvis.Core.Configuration;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Options;

namespace Jarvis.App.Views;

public partial class PointerOverlayWindow : Window
{
    private const int TrailLength = 14;
    private static readonly Color DefaultColor = Color.FromRgb(0x00, 0xE5, 0xFF);

    private readonly List<Point> _trail = [];
    private readonly List<Ellipse> _trailDots = [];
    private readonly DispatcherTimer _hideTimer;
    private Int32Rect _virtualScreen;
    private Matrix _fromDevice = Matrix.Identity;
    private Point _position;
    private bool _visible;
    private Flight? _flight;

    public PointerOverlayWindow(IOptions<JarvisOptions> options)
    {
        InitializeComponent();
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _hideTimer.Tick += (_, _) => HidePointer();

        for (var i = 0; i < TrailLength; i++)
        {
            var dot = new Ellipse { Fill = new SolidColorBrush(DefaultColor), IsHitTestVisible = false, Opacity = 0 };
            _trailDots.Add(dot);
            TrailLayer.Children.Add(dot);
        }

        SourceInitialized += (_, _) =>
        {
            Win32.MakeOverlay(this, clickThrough: true, options.Value.Ui.HideFromScreenCapture);
            CoverVirtualScreen();
        };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(CoverVirtualScreen);
        CompositionTarget.Rendering += OnRendering;
    }

    public Point HomePhysical { get; set; }

    public Task AnimateAsync(ScreenRect target, PointerAction action, string? label)
    {
        var destination = ToLocal(new Point(target.CenterX, target.CenterY));
        if (!_visible)
        {
            _position = ToLocal(HomePhysical);
            _trail.Clear();
            ShowPointer();
        }

        _hideTimer.Stop();
        SetLabel(label);
        SetColor(ColorFor(action));

        var distance = (destination - _position).Length;
        var duration = TimeSpan.FromMilliseconds(Math.Clamp(distance * 0.45, 180, 450));
        var bend = (destination - _position) is var d && d.Length > 0
            ? new Vector(-d.Y, d.X) / d.Length * Math.Min(120, distance * 0.22) * (Random.Shared.Next(2) == 0 ? 1 : -1)
            : new Vector();
        var control = _position + (destination - _position) / 2 + bend;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _flight?.Completion.TrySetResult();
        _flight = new Flight(_position, control, destination, DateTime.UtcNow, duration, action, completion);
        return completion.Task;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_flight is { } flight)
        {
            var t = Math.Clamp((DateTime.UtcNow - flight.Start).TotalMilliseconds / flight.Duration.TotalMilliseconds, 0, 1);
            var eased = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            _position = Bezier(flight.From, flight.Control, flight.To, eased);

            if (t >= 1)
            {
                _flight = null;
                _position = flight.To;
                PlayEffect(flight.Action);
                CompleteAfter(flight.Completion, flight.Action == PointerAction.Move ? 0 : 80);
                _hideTimer.Start();
            }
        }

        if (!_visible)
        {
            return;
        }

        Canvas.SetLeft(Pointer, _position.X);
        Canvas.SetTop(Pointer, _position.Y);
        UpdateTrail();
    }

    private void UpdateTrail()
    {
        _trail.Insert(0, _position);
        if (_trail.Count > TrailLength)
        {
            _trail.RemoveAt(_trail.Count - 1);
        }

        for (var i = 0; i < _trailDots.Count; i++)
        {
            var dot = _trailDots[i];
            if (i >= _trail.Count || i == 0 || (_trail[i] - _trail[0]).Length < 2)
            {
                dot.Opacity = 0;
                continue;
            }

            var size = 9.0 * (1 - i / (double)TrailLength) + 2;
            dot.Width = dot.Height = size;
            dot.Opacity = 0.45 * (1 - i / (double)TrailLength);
            Canvas.SetLeft(dot, _trail[i].X - size / 2 + 4);
            Canvas.SetTop(dot, _trail[i].Y - size / 2 + 8);
        }
    }

    private void PlayEffect(PointerAction action)
    {
        if (action == PointerAction.Move)
        {
            return;
        }

        var press = new DoubleAnimation(1, 0.78, TimeSpan.FromMilliseconds(90)) { AutoReverse = true };
        ArrowScale.BeginAnimation(ScaleTransform.ScaleXProperty, press);
        ArrowScale.BeginAnimation(ScaleTransform.ScaleYProperty, press);

        SpawnRipple(TimeSpan.Zero);
        if (action is PointerAction.DoubleClick)
        {
            SpawnRipple(TimeSpan.FromMilliseconds(140));
        }
    }

    private void SpawnRipple(TimeSpan delay)
    {
        const double size = 70;
        var color = ArrowGlow.Color;
        var ripple = new Ellipse
        {
            Width = size,
            Height = size,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 3,
            Fill = new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B)),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.1, 0.1),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(ripple, _position.X - size / 2);
        Canvas.SetTop(ripple, _position.Y - size / 2);
        RippleLayer.Children.Add(ripple);

        var duration = TimeSpan.FromMilliseconds(520);
        var grow = new DoubleAnimation(0.1, 1, duration) { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var fade = new DoubleAnimationUsingKeyFrames { BeginTime = delay };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(duration)));
        fade.Completed += (_, _) => RippleLayer.Children.Remove(ripple);

        var scale = (ScaleTransform)ripple.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        ripple.BeginAnimation(OpacityProperty, fade);
    }

    private void CompleteAfter(TaskCompletionSource completion, int milliseconds)
    {
        if (milliseconds == 0)
        {
            completion.TrySetResult();
            return;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            completion.TrySetResult();
        };
        timer.Start();
    }

    private void ShowPointer()
    {
        _visible = true;
        Pointer.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
    }

    private void HidePointer()
    {
        _hideTimer.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(600));
        fade.Completed += (_, _) =>
        {
            if (_hideTimer.IsEnabled || _flight is not null)
            {
                return;
            }

            _visible = false;
            _trail.Clear();
            _trailDots.ForEach(d => d.Opacity = 0);
        };
        Pointer.BeginAnimation(OpacityProperty, fade);
    }

    private void SetLabel(string? label)
    {
        LabelText.Text = label ?? string.Empty;
        Label.BeginAnimation(OpacityProperty, new DoubleAnimation(string.IsNullOrWhiteSpace(label) ? 0 : 1, TimeSpan.FromMilliseconds(150)));
    }

    private void SetColor(Color color)
    {
        ArrowGlow.Color = color;
        AccentStroke.Color = color;
        ArrowStroke.Color = color;
        GlowStop.Color = Color.FromArgb(0x80, color.R, color.G, color.B);
        foreach (var dot in _trailDots)
        {
            ((SolidColorBrush)dot.Fill).Color = color;
        }
    }

    private static Color ColorFor(PointerAction action) => action switch
    {
        PointerAction.Type => Color.FromRgb(0xB3, 0x88, 0xFF),
        PointerAction.RightClick => Color.FromRgb(0xFF, 0xB0, 0x20),
        PointerAction.Scroll => Color.FromRgb(0x00, 0xFF, 0xA3),
        _ => DefaultColor,
    };

    private static Point Bezier(Point a, Point c, Point b, double t) =>
        new((1 - t) * (1 - t) * a.X + 2 * (1 - t) * t * c.X + t * t * b.X,
            (1 - t) * (1 - t) * a.Y + 2 * (1 - t) * t * c.Y + t * t * b.Y);

    private Point ToLocal(Point physical) =>
        _fromDevice.Transform(new Point(physical.X - _virtualScreen.X, physical.Y - _virtualScreen.Y));

    private void CoverVirtualScreen()
    {
        _virtualScreen = Win32.VirtualScreen;
        Win32.SetBoundsPhysical(this, _virtualScreen);
        _fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
    }

    private sealed record Flight(Point From, Point Control, Point To, DateTime Start, TimeSpan Duration, PointerAction Action, TaskCompletionSource Completion);
}
