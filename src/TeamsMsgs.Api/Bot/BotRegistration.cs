// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Extensions.Configuration;

namespace TeamsMsgs.Api.Bot;

/// <summary>Bot Framework wire-up. Uses Microsoft.Bot.Builder 4.x CloudAdapter (MSI-friendly).</summary>
public static class BotRegistration
{
    public static IServiceCollection AddBotFramework(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<BotFrameworkAuthentication>(_ =>
            new ConfigurationBotFrameworkAuthentication(configuration));

        services.AddSingleton<CloudAdapter>(sp => new CloudAdapter(
            sp.GetRequiredService<BotFrameworkAuthentication>(),
            sp.GetRequiredService<ILogger<CloudAdapter>>()));
        services.AddSingleton<IBotFrameworkHttpAdapter>(sp => sp.GetRequiredService<CloudAdapter>());

        services.AddTransient<IBot, TeamsMsgs.Shared.Bot.ProactiveBot>();

        return services;
    }
}
