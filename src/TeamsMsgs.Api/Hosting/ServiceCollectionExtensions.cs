// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

using Microsoft.Bot.Builder;
using TeamsMsgs.Shared.Azure;
using TeamsMsgs.Shared.Bot;
using TeamsMsgs.Shared.Configuration;
using TeamsMsgs.Shared.Jobs;
using TeamsMsgs.Shared.Queueing;
using TeamsMsgs.Shared.Storage;

namespace TeamsMsgs.Api.Hosting;

/// <summary>Shared DI registration for storage, jobs, queue and bot refs.</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSharedServices(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<StorageOptions>().Bind(configuration.GetSection(StorageOptions.SectionName));
        services.AddOptions<BotOptions>().Bind(configuration.GetSection(BotOptions.SectionName));
        services.AddOptions<ApiOptions>().Bind(configuration.GetSection(ApiOptions.SectionName));

        services.AddSingleton<AzureClientFactory>();
        services.AddSingleton<IConversationRefStore, ConversationRefStore>();
        services.AddSingleton<IJobTracker, TableJobTracker>();
        services.AddSingleton<ISentMarkStore, TableSentMarkStore>();
        services.AddSingleton<ISendQueue, StorageSendQueue>();
        services.AddSingleton<IRefStore, ConversationRefStoreAdapter>();

        return services;
    }
}

/// <summary>Route group helper for x-api-key.</summary>
public static class ApiKeyRouteExtensions
{
    public static RouteGroupBuilder RequireApiKey(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.AddEndpointFilterFactory((_, next) => async invocationContext =>
        {
            await TeamsMsgs.Api.Auth.ApiKeyEndpointFilter
                .ValidateAsync(invocationContext.HttpContext)
                .ConfigureAwait(false);
            if (invocationContext.HttpContext.Response.StatusCode == StatusCodes.Status401Unauthorized)
            {
                return Results.Unauthorized();
            }
            return await next(invocationContext).ConfigureAwait(false);
        });
    }
}
