using System.Windows;
using System.Windows.Input;

namespace Jarvis.App.Views;

/// <summary>Type a command instead of speaking it. Enter sends, Esc closes.</summary>
public partial class CommandWindow : Window
{
    public CommandWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Activate();
            Input.Focus();
        };
        Closing += (_, _) => _closing = true;
        Deactivated += (_, _) => SafeClose();
        Input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                SafeClose();
            }
            else if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(Input.Text))
            {
                var text = Input.Text.Trim();
                SafeClose();
                Submitted?.Invoke(text);
            }
        };
    }

    public event Action<string>? Submitted;

    private bool _closing;

    private void SafeClose()
    {
        if (!_closing)
        {
            Close();
        }
    }
}
