using System.Windows;

namespace Jarvis.App.Views;

public partial class ConfirmWindow : Window
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConfirmWindow(string action, string reason, string? title = null)
    {
        InitializeComponent();
        if (title is not null)
        {
            TitleText.Text = title;
        }

        ActionText.Text = action;
        ReasonText.Text = reason;
        AllowButton.Click += (_, _) => Finish(true);
        DenyButton.Click += (_, _) => Finish(false);
        Closing += (_, _) => _closing = true;
        Closed += (_, _) => _result.TrySetResult(false);
        Loaded += (_, _) =>
        {
            Activate();
            AllowButton.Focus();
        };
    }

    public Task<bool> Result => _result.Task;

    private bool _closing;

    public void Finish(bool allowed)
    {
        _result.TrySetResult(allowed);
        if (IsLoaded && !_closing)
        {
            Close();
        }
    }
}
