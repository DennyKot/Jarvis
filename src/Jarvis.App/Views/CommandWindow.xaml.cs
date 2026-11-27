using System.Windows;
using System.Windows.Input;

namespace Jarvis.App.Views;

public partial class CommandWindow : Window
{
    public CommandWindow(string? initialText = null)
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Activate();
            Input.Focus();
            if (initialText is not null)
            {
                Input.Text = initialText;
                Input.CaretIndex = initialText.Length;
            }
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
