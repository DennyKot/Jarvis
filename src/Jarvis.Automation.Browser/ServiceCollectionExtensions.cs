using Jarvis.Automation.Browser.Extension;
using Jarvis.Core.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Automation.Browser;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddJarvisBrowserAutomation(this IServiceCollection services)
    {
        services.AddSingleton<ChromeSession>();
        services.AddSingleton<IToolProvider, BrowserToolProvider>();
        services.AddSingleton<ExtensionBridge>();
        services.AddSingleton<ChromeExtensionInstaller>();
        services.AddSingleton<IToolProvider, ExtensionBrowserToolProvider>();
        return services;
    }
}
