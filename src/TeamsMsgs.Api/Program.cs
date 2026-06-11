// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

using TeamsMsgs.Api.Auth;
using TeamsMsgs.Api.Bot;
using TeamsMsgs.Api.Endpoints;
using TeamsMsgs.Api.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSharedServices(builder.Configuration);
builder.Services.AddBotFramework(builder.Configuration);

var app = builder.Build();

// Bot endpoint não exige x-api-key (auth do próprio Bot Framework).
app.MapBotEndpoint();
app.MapHealthEndpoints();

// /api/send, /api/jobs/*, /api/status atrás do x-api-key
app.MapGroup("/api")
    .RequireApiKey()
    .MapSendEndpoint()
    .MapJobsEndpoint();

app.Logger.LogInformation("🤖 Teams Proactive Messaging API (.NET 8) listening");

app.Run();
