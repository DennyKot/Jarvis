using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using H.NotifyIcon;
using Jarvis.App.Logging;
using Jarvis.App.Services;
using Jarvis.App.Views;
using Jarvis.Automation.Browser;
using Jarvis.Automation.Browser.Extension;
using Jarvis.Automation.Desktop;
using Jarvis.Core;
using Jarvis.Core.Llm;
using Jarvis.Core.Safety;
using Jarvis.Core.Tools;
using Jarvis.Voice;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jarvis.App;

public partial class App : Application
{
    private const string InstallExtensionArg = "--install-extension";

    private Mutex? _singleInstance;
    private IHost? _host;
    private TaskbarIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, "Jarvis.Assistant.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Jarvis is already running (look for the robot in the bottom-right corner).", "Jarvis");
            Shutdown();
            return;
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = e.Args.Where(a => a != InstallExtensionArg).ToArray(),
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration.AddUserSecrets<App>(optional: true);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FileLoggerProvider(FileLoggerProvider.DefaultDirectory));

        builder.Services
            .AddJarvisCore(builder.Configuration)
            .AddJarvisVoice()
            .AddJarvisBrowserAutomation()
            .AddJarvisDesktopAutomation();

        builder.Services.AddSingleton<AvatarWindow>();
        builder.Services.AddSingleton<PointerOverlayWindow>();
        builder.Services.AddSingleton<PointerService>();
        builder.Services.AddSingleton<IPointerService>(sp => sp.GetRequiredService<PointerService>());
        builder.Services.AddSingleton<IConfirmationService, ConfirmationService>();
        builder.Services.AddSingleton<GlobalHotkeys>();
        builder.Services.AddSingleton<JarvisController>();

        _host = builder.Build();
        var logger = _host.Services.GetRequiredService<ILogger<App>>();
        DispatcherUnhandledException += (_, args) =>
        {
            logger.LogError(args.Exception, "Unhandled UI exception");
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            logger.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        var overlay = _host.Services.GetRequiredService<PointerOverlayWindow>();
        overlay.Show();

        var avatar = _host.Services.GetRequiredService<AvatarWindow>();
        avatar.Show();

        // The Chrome extension reconnects on its own whenever Jarvis is running.
        _host.Services.GetRequiredService<ExtensionBridge>().Start();
        var profiles = _host.Services.GetRequiredService<Jarvis.Core.Agent.UserProfileService>();
        _ = Task.Run(() => profiles.Profile);

        // Download/load speech models up front instead of on the first voice command.
        var transcriber = _host.Services.GetRequiredService<Jarvis.Voice.ISpeechTranscriber>();
        _ = Task.Run(async () =>
        {
            try
            {
                await transcriber.PrepareAsync(CancellationToken.None);
                logger.LogInformation("Speech models ready");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Speech model preload failed; will retry on first use");
            }
        });

        var controller = _host.Services.GetRequiredService<JarvisController>();
        controller.Start();
        _host.Services.GetRequiredService<PointerService>().SetHome(avatar.CharacterCenterPhysical);

        _tray = CreateTrayIcon(controller, avatar);
        logger.LogInformation("Jarvis started");

        if (e.Args.Contains(InstallExtensionArg))
        {
            controller.InstallExtension();
        }

        _ = RunFirstRunSetupAsync(logger, force: false);

        // Jarvis.exe --command "open notepad and type hello"
        if (_host.Services.GetRequiredService<IConfiguration>()["command"] is { Length: > 0 } startupCommand)
        {
            controller.Submit(startupCommand);
        }
    }

    private static readonly string SetupMarker =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jarvis", "setup.done");

    private SetupWindow? _setupWindow;

    /// <summary>Offers to install Ollama + the model on first run (or on demand from the tray menu).</summary>
    private async Task RunFirstRunSetupAsync(ILogger logger, bool force)
    {
        var setup = _host!.Services.GetRequiredService<OllamaSetup>();
        if (_setupWindow is not null || !setup.IsOllamaProvider || (!force && System.IO.File.Exists(SetupMarker)))
        {
            _setupWindow?.Activate();
            return;
        }

        if (await setup.IsReadyAsync())
        {
            MarkSetupDone();
            if (force)
            {
                MessageBox.Show($"Ollama and {setup.Model} are already installed.", "Jarvis");
            }

            return;
        }

        _setupWindow = new SetupWindow(setup, logger);
        _setupWindow.Closed += (_, _) =>
        {
            if (_setupWindow.Completed)
            {
                MarkSetupDone();
            }

            _setupWindow = null;
        };
        _setupWindow.Show();
    }

    private static void MarkSetupDone()
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SetupMarker)!);
        System.IO.File.WriteAllText(SetupMarker, DateTime.UtcNow.ToString("O"));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        if (_host is not null)
        {
            _host.Services.GetRequiredService<GlobalHotkeys>().Dispose();
            _host.Services.GetRequiredService<ChromeSession>().DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            _host.Services.GetRequiredService<ExtensionBridge>().DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            _host.Dispose();
        }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private TaskbarIcon CreateTrayIcon(JarvisController controller, AvatarWindow avatar)
    {
        MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }

        var menu = new ContextMenu();
        menu.Items.Add(Item("🎙  Talk", controller.Talk));
        menu.Items.Add(Item("■  Stop", controller.Stop));
        menu.Items.Add(Item("🧩  Install Chrome extension", controller.InstallExtension));
        menu.Items.Add(Item("🦙  Install Ollama + model", () => _ = RunFirstRunSetupAsync(_host!.Services.GetRequiredService<ILogger<App>>(), force: true)));
        menu.Items.Add(Item("👁  Show / hide Jarvis", () => avatar.Visibility = avatar.IsVisible ? Visibility.Hidden : Visibility.Visible));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit", Shutdown));

        var status = _host!.Services.GetRequiredService<Jarvis.Core.Agent.AssistantStatus>();
        var tray = new TaskbarIcon
        {
            ToolTipText = TrayIconRenderer.Describe(status.State),
            ContextMenu = menu,
            IconSource = TrayIconRenderer.Render(status.State),
        };
        status.StateChanged += state => Dispatcher.BeginInvoke(() =>
        {
            tray.IconSource = TrayIconRenderer.Render(state);
            tray.ToolTipText = TrayIconRenderer.Describe(state);
        });
        tray.TrayLeftMouseUp += (_, _) => controller.Talk();
        tray.ForceCreate();
        return tray;
    }
}
