// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using TeamsMsgs.Shared.Azure;
using TeamsMsgs.Shared.Bot;
using TeamsMsgs.Shared.Configuration;
using TeamsMsgs.Shared.Jobs;
using TeamsMsgs.Shared.Queueing;
using TeamsMsgs.Shared.Storage;
using TeamsMsgs.Worker.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<StorageOptions>().Bind(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.AddOptions<BotOptions>().Bind(builder.Configuration.GetSection(BotOptions.SectionName));
builder.Services.AddOptions<WorkerOptions>().Bind(builder.Configuration.GetSection(WorkerOptions.SectionName));

builder.Services.AddMemoryCache();

builder.Services.AddSingleton<AzureClientFactory>();
builder.Services.AddSingleton<IConversationRefStore, ConversationRefStore>();
builder.Services.AddSingleton<IJobTracker, TableJobTracker>();
builder.Services.AddSingleton<ISentMarkStore, TableSentMarkStore>();
builder.Services.AddSingleton<ISendQueueReceiver, StorageSendQueueReceiver>();
builder.Services.AddSingleton<IRefStore, ConversationRefStoreAdapter>();

builder.Services.AddSingleton<BotFrameworkAuthentication>(_ =>
    new ConfigurationBotFrameworkAuthentication(builder.Configuration));
builder.Services.AddSingleton<CloudAdapter>(sp => new CloudAdapter(
    sp.GetRequiredService<BotFrameworkAuthentication>(),
    sp.GetRequiredService<ILogger<CloudAdapter>>()));
builder.Services.AddTransient<IBot, ProactiveBot>();

builder.Services.AddHostedService<QueueConsumerService>();

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
