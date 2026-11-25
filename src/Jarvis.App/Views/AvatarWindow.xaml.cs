using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Jarvis.App.Interop;
using Jarvis.App.Services;
using Jarvis.Core.Agent;
using Jarvis.Core.Configuration;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Options;

namespace Jarvis.App.Views;

public partial class AvatarWindow : Window
{
    private readonly DispatcherTimer _bubbleTimer;
    private readonly RobotSounds _sounds;
    private bool _bubbleVisible;

    public AvatarWindow(IOptions<JarvisOptions> options)
    {
        InitializeComponent();
        _sounds = new RobotSounds(options.Value.Ui.Sounds);
        _bubbleTimer = new DispatcherTimer();
        _bubbleTimer.Tick += (_, _) => HideBubble();

        SourceInitialized += (_, _) => Win32.MakeOverlay(this, clickThrough: false, options.Value.Ui.HideFromScreenCapture);
        Loaded += (_, _) => DockBottomRight();

        CharacterHost.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                TalkRequested?.Invoke();
                return;
            }

            DragMove();
            Moved?.Invoke(this, EventArgs.Empty);
        };

        TalkItem.Click += (_, _) => TalkRequested?.Invoke();
        TypeItem.Click += (_, _) => TypeRequested?.Invoke();
        StopItem.Click += (_, _) => StopRequested?.Invoke();
        UndoItem.Click += (_, _) => UndoRequested?.Invoke();
        DryRunItem.Click += (_, _) => DryRunRequested?.Invoke();
        ResetItem.Click += (_, _) => ResetRequested?.Invoke();
        ExtensionItem.Click += (_, _) => ExtensionInstallRequested?.Invoke();
        SettingsItem.Click += (_, _) => SettingsRequested?.Invoke();
        LogsItem.Click += (_, _) => LogsRequested?.Invoke();
        ExitItem.Click += (_, _) => ExitRequested?.Invoke();

        var pulse = new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(600)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        ActivityDot.BeginAnimation(OpacityProperty, pulse);

        // Keep the bubble open while the user reaches for a source link.
        Bubble.MouseEnter += (_, _) => _bubbleTimer.Stop();
        Bubble.MouseLeave += (_, _) => ScheduleHide(TimeSpan.FromSeconds(4));
    }

    public event Action? TalkRequested;
    public event Action? TypeRequested;
    public event Action? StopRequested;
    public event Action? UndoRequested;
    public event Action? DryRunRequested;
    public event Action? ResetRequested;
    public event Action? ExtensionInstallRequested;
    public event Action? SettingsRequested;
    public event Action? LogsRequested;
    public event Action? ExitRequested;

    /// <summary>Center of the character in physical screen pixels (where the AI pointer is "launched" from).</summary>
    public Point CharacterCenterPhysical
    {
        get
        {
            var center = CharacterHost.PointToScreen(new Point(CharacterHost.ActualWidth / 2, CharacterHost.ActualHeight / 3));
            return center;
        }
    }

    public void SetState(AssistantState state)
    {
        if (Character.State != state)
        {
            _sounds.OnStateChanged(Character.State, state);
        }

        Character.State = state;
        var useLottie = Lottie.HasClip(state) || Lottie.HasClip(AssistantState.Idle);
        Lottie.Visibility = useLottie ? Visibility.Visible : Visibility.Collapsed;
        Character.Visibility = useLottie ? Visibility.Collapsed : Visibility.Visible;
        if (useLottie)
        {
            Lottie.Play(state);
        }

        ActivityDot.Fill = new SolidColorBrush(state switch
        {
            AssistantState.Listening => Color.FromRgb(0xFF, 0x4D, 0x8D),
            AssistantState.Confirming => Color.FromRgb(0xFF, 0xB0, 0x20),
            AssistantState.Error => Color.FromRgb(0xFF, 0x3B, 0x3B),
            _ => Color.FromRgb(0x00, 0xE5, 0xFF),
        });

        if (state == AssistantState.Idle)
        {
            ActivityPanel.Visibility = Visibility.Collapsed;
            ScheduleHide(ReplyText.Visibility == Visibility.Visible ? TimeSpan.FromSeconds(12) : TimeSpan.FromSeconds(3));
        }
    }

    public void SetAudioLevel(float level) => Character.AudioLevel = level;

    public void LookAt(ScreenRect target)
    {
        var center = CharacterCenterPhysical;
        Character.LookTowards(new Vector(target.CenterX - center.X, target.CenterY - center.Y));
    }

    public void ShowUserText(string text)
    {
        UserText.Text = $"“{text}”";
        UserText.Visibility = Visibility.Visible;
        ReplyText.Visibility = Visibility.Collapsed;
        ShowBubble();
    }

    public void ShowActivity(string text)
    {
        ActivityText.Text = text;
        ActivityPanel.Visibility = Visibility.Visible;
        ShowBubble();
    }

    public void ShowReply(string text, bool isError = false)
    {
        SetReplyText(text);
        ReplyText.Foreground = isError ? new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x8A)) : Brushes.White;
        ReplyText.Visibility = Visibility.Visible;
        ActivityPanel.Visibility = Visibility.Collapsed;
        ShowBubble();
        ScheduleHide(TimeSpan.FromSeconds(Math.Clamp(text.Length / 12.0, 6, 25)));
    }

    public void ShowPartialReply(string text)
    {
        SetReplyText(text);
        ReplyText.Foreground = Brushes.White;
        ReplyText.Visibility = Visibility.Visible;
        ActivityPanel.Visibility = Visibility.Collapsed;
        ShowBubble();
    }

    /// <summary>Renders [Site](url) and bare URLs as clickable links that open in the default browser.</summary>
    private void SetReplyText(string text)
    {
        ReplyText.Inlines.Clear();
        foreach (var segment in ReplyLinks.Parse(text))
        {
            if (segment.Url is null)
            {
                ReplyText.Inlines.Add(new System.Windows.Documents.Run(segment.Text));
                continue;
            }

            var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(segment.Text))
            {
                NavigateUri = segment.Url,
                Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF)),
                Cursor = Cursors.Hand,
                ToolTip = segment.Url.ToString(),
            };
            link.RequestNavigate += (_, e) =>
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                e.Handled = true;
            };
            ReplyText.Inlines.Add(link);
        }
    }

    public void ClearBubble()
    {
        UserText.Visibility = ActivityPanel.Visibility = ReplyText.Visibility = Visibility.Collapsed;
    }

    private void ShowBubble()
    {
        _bubbleTimer.Stop();
        if (_bubbleVisible)
        {
            return;
        }

        _bubbleVisible = true;
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
        Bubble.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        BubbleTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
    }

    private void HideBubble()
    {
        _bubbleTimer.Stop();
        _bubbleVisible = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400));
        fade.Completed += (_, _) =>
        {
            if (!_bubbleVisible)
            {
                ClearBubble();
            }
        };
        Bubble.BeginAnimation(OpacityProperty, fade);
    }

    private void ScheduleHide(TimeSpan after)
    {
        _bubbleTimer.Stop();
        _bubbleTimer.Interval = after;
        _bubbleTimer.Start();
    }

    private void DockBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 8;
        Top = area.Bottom - Height - 4;
        Moved?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Moved;
}
