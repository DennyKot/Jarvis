using Jarvis.Automation.Desktop.Apps;
using Jarvis.Automation.Desktop.Files;
using Jarvis.Automation.Desktop.Input;
using Jarvis.Automation.Desktop.Screen;
using Jarvis.Automation.Desktop.UiAutomation;
using Jarvis.Automation.Desktop.Windows;
using Jarvis.Core.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Automation.Desktop;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddJarvisDesktopAutomation(this IServiceCollection services)
    {
        services.AddSingleton<InputSimulator>();
        services.AddSingleton<WindowService>();
        services.AddSingleton<IWindowActivator>(sp => sp.GetRequiredService<WindowService>());
        services.AddSingleton<AppLauncher>();
        services.AddSingleton<UiAutomationService>();
        services.AddSingleton<ScreenCaptureService>();
        services.AddSingleton<OcrService>();
        services.AddSingleton<IToolProvider, DesktopToolProvider>();
        services.AddSingleton<IToolProvider, ClipboardFileToolProvider>();
        return services;
    }
}
