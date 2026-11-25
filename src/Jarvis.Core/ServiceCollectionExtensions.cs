using Jarvis.Core.Agent;
using Jarvis.Core.Configuration;
using Jarvis.Core.Llm;
using Jarvis.Core.Safety;
using Jarvis.Core.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jarvis.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddJarvisCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JarvisOptions>(configuration.GetSection(JarvisOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IChatClientFactory, ChatClientFactory>();
        services.AddSingleton<OllamaSetup>();
        services.TryAddSingleton<ILlmWarmup, LlmWarmup>();
        services.TryAddSingleton<IPointerService, NullPointerService>();
        services.TryAddSingleton<IWindowActivator, NullWindowActivator>();
        services.AddSingleton<AssistantStatus>();
        services.AddSingleton<AgentObservations>();
        services.AddSingleton<UserProfileService>();
        services.AddSingleton<RiskPolicy>();
        services.AddSingleton<IActionGuard, ActionGuard>();
        services.AddSingleton<UndoJournal>();
        services.AddSingleton<IToolProvider, UndoToolProvider>();
        services.AddSingleton<IToolProvider, WebLookupToolProvider>();
        services.AddSingleton<IAgentRunner, AgentRunner>();
        return services;
    }
}
