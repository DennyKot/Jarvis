using System.Windows;
using Jarvis.Core.Llm;
using Microsoft.Extensions.Logging;

namespace Jarvis.App.Views;

/// <summary>First-run window that installs Ollama and pulls the configured model (checked by default).</summary>
public partial class SetupWindow : Window
{
    private readonly OllamaSetup _setup;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;

    public SetupWindow(OllamaSetup setup, ILogger logger)
    {
        InitializeComponent();
        _setup = setup;
        _logger = logger;
        InstallCheckBox.Content = $"Install Ollama and the {setup.Model} model (several GB download)";
        ContinueButton.Click += async (_, _) => await ContinueAsync();
        CancelButton.Click += (_, _) => Close();
        Closed += (_, _) => _cts?.Cancel();
        MouseLeftButtonDown += (_, _) => DragMove();
    }

    /// <summary>True when the user finished or explicitly declined setup (don't ask again).</summary>
    public bool Completed { get; private set; }

    private async Task ContinueAsync()
    {
        if (InstallCheckBox.IsChecked != true)
        {
            Completed = true;
            Close();
            return;
        }

        _cts = new CancellationTokenSource();
        InstallCheckBox.IsEnabled = false;
        ContinueButton.IsEnabled = false;
        CancelButton.Content = "Cancel";
        ProgressPanel.Visibility = Visibility.Visible;
        var progress = new Progress<(string Status, double? Percent)>(p =>
        {
            StatusText.Text = p.Status;
            Progress.IsIndeterminate = p.Percent is null;
            Progress.Value = p.Percent ?? 0;
        });

        try
        {
            await _setup.RunAsync(progress, _cts.Token);
            Completed = true;
            Close();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ollama setup failed");
            StatusText.Text = $"Setup failed: {ex.Message}";
            Progress.IsIndeterminate = false;
            InstallCheckBox.IsEnabled = true;
            ContinueButton.IsEnabled = true;
            ContinueButton.Content = "Retry";
            CancelButton.Content = "Skip";
        }
    }
}
