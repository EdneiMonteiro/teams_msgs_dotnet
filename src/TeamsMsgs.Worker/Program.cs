// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

using Azure.Messaging.ServiceBus;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TeamsMsgs.Shared.Azure;
using TeamsMsgs.Shared.Bot;
using TeamsMsgs.Shared.Configuration;
using TeamsMsgs.Shared.Jobs;
using TeamsMsgs.Shared.Messaging;
using TeamsMsgs.Shared.RateLimiting;
using TeamsMsgs.Shared.Redis;
using TeamsMsgs.Shared.Storage;
using TeamsMsgs.Worker.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<StorageOptions>().Bind(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.AddOptions<ServiceBusOptions>().Bind(builder.Configuration.GetSection(ServiceBusOptions.SectionName));
builder.Services.AddOptions<RedisOptions>().Bind(builder.Configuration.GetSection(RedisOptions.SectionName));
builder.Services.AddOptions<RateLimitOptions>().Bind(builder.Configuration.GetSection(RateLimitOptions.SectionName));
builder.Services.AddOptions<BotOptions>().Bind(builder.Configuration.GetSection(BotOptions.SectionName));
builder.Services.AddOptions<WorkerOptions>().Bind(builder.Configuration.GetSection(WorkerOptions.SectionName));

builder.Services.AddMemoryCache();

builder.Services.AddSingleton<AzureClientFactory>();
builder.Services.AddSingleton<IRedisConnection, RedisConnection>();
builder.Services.AddSingleton(sp =>
    new ServiceBusClient(sp.GetRequiredService<IOptions<ServiceBusOptions>>().Value.ConnectionString));
builder.Services.AddSingleton<IConversationRefStore, ConversationRefStore>();
builder.Services.AddSingleton<IJobTracker, RedisJobTracker>();
builder.Services.AddSingleton<ISendQueueReceiver, ServiceBusSendQueueReceiver>();
builder.Services.AddSingleton<IRefStore, ConversationRefStoreAdapter>();
builder.Services.AddSingleton<IRateLimiter>(sp =>
{
    var o = sp.GetRequiredService<IOptions<RateLimitOptions>>().Value;
    return new RedisTokenBucket(sp.GetRequiredService<IRedisConnection>(), o.Key, o.Capacity, o.RatePerSec);
});

builder.Services.AddSingleton<BotFrameworkAuthentication>(_ =>
    new ConfigurationBotFrameworkAuthentication(builder.Configuration));
builder.Services.AddSingleton<CloudAdapter>(sp => new CloudAdapter(
    sp.GetRequiredService<BotFrameworkAuthentication>(),
    sp.GetRequiredService<ILogger<CloudAdapter>>()));
builder.Services.AddTransient<IBot, ProactiveBot>();

builder.Services.AddHostedService<QueueConsumerService>();

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
