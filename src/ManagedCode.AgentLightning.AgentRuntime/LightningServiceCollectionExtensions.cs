using System;
using ManagedCode.AgentLightning.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace ManagedCode.AgentLightning.AgentRuntime;

/// <summary>
/// Dependency injection helpers for wiring Agent Lightning services.
/// </summary>
public static class LightningServiceCollectionExtensions
{
    public static IServiceCollection AddLightningAgent(
        this IServiceCollection services,
        Action<LightningAgentOptions> configureOptions)
    {
        if (configureOptions is null)
        {
            throw new ArgumentNullException(nameof(configureOptions));
        }

        services.AddOptions<LightningAgentOptions>()
            .Configure(configureOptions);

        services.AddSingleton(serviceProvider => new LightningAgent(
            serviceProvider.GetRequiredService<IChatClient>(),
            serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<LightningAgentOptions>>(),
            serviceProvider.GetServices<Hook>(),
            serviceProvider.GetRequiredService<ILogger<LightningAgent>>(),
            serviceProvider.GetService<TimeProvider>()));

        return services;
    }

    public static IServiceCollection AddLightningAgent(
        this IServiceCollection services,
        Action<LightningAgentOptions> configureOptions,
        Func<IServiceProvider, IChatClient> chatClientFactory)
    {
        if (configureOptions is null)
        {
            throw new ArgumentNullException(nameof(configureOptions));
        }

        if (chatClientFactory is null)
        {
            throw new ArgumentNullException(nameof(chatClientFactory));
        }

        services.AddSingleton<IChatClient>(chatClientFactory);
        return services.AddLightningAgent(configureOptions);
    }


    public static IServiceCollection AddLightningAgentFromAgentFramework(
        this IServiceCollection services,
        Action<LightningAgentOptions> configureOptions,
        Func<IServiceProvider, AIAgent> agentFactory)
    {
        if (configureOptions is null)
        {
            throw new ArgumentNullException(nameof(configureOptions));
        }

        if (agentFactory is null)
        {
            throw new ArgumentNullException(nameof(agentFactory));
        }

        services.AddOptions<LightningAgentOptions>().Configure(configureOptions);
        services.AddSingleton(serviceProvider => new LightningAgent(
            agentFactory(serviceProvider),
            serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<LightningAgentOptions>>().Value,
            serviceProvider.GetServices<Hook>(),
            serviceProvider.GetRequiredService<ILogger<LightningAgent>>(),
            serviceProvider.GetService<TimeProvider>()));
        return services;
    }

    public static IServiceCollection AddLightningHooks(this IServiceCollection services, params Hook[] hooks)
    {
        if (hooks is null)
        {
            throw new ArgumentNullException(nameof(hooks));
        }

        foreach (var hook in hooks)
        {
            services.AddSingleton<Hook>(hook);
        }

        return services;
    }
}
