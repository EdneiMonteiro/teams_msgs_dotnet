// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

using TeamsMsgs.Shared.Configuration;
using TeamsMsgs.Shared.Jobs;
using TeamsMsgs.Shared.Storage;
using Microsoft.Extensions.Options;

namespace TeamsMsgs.Api.Endpoints;

public static class JobsEndpoint
{
    public static IEndpointRouteBuilder MapJobsEndpoint(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/jobs/{id}", async (string id, IJobTracker jobs, CancellationToken ct) =>
        {
            var job = await jobs.GetAsync(id, ct).ConfigureAwait(false);
            return job is null
                ? Results.NotFound(new { error = "Job não encontrado" })
                : Results.Ok(job);
        });

        app.MapGet("/status", async (
            IConversationRefStore refs,
            IOptions<ServiceBusOptions> serviceBusOptions,
            CancellationToken ct) =>
        {
            var count = await refs.CountAsync(ct).ConfigureAwait(false);
            return Results.Ok(new
            {
                registeredUsers = count,
                status = "running",
                mode = "servicebus",
                queue = serviceBusOptions.Value.QueueName,
            });
        });

        return app;
    }
}
