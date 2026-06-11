// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

using TeamsMsgs.Shared.Jobs;
using TeamsMsgs.Shared.Queueing;
using TeamsMsgs.Shared.Storage;

namespace TeamsMsgs.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/healthz", () => Results.Ok(new
        {
            status = "ok",
            uptimeSeconds = (long)(DateTimeOffset.UtcNow - Process.StartTime).TotalSeconds,
        }));

        app.MapGet("/readyz", async (
            IConversationRefStore refs,
            IJobTracker jobs,
            ISendQueue queue,
            CancellationToken ct) =>
        {
            var storageTask = refs.PingAsync(ct);
            var jobsTask = jobs.PingAsync(ct);
            var queueTask = queue.PingAsync(ct);
            await Task.WhenAll(storageTask, jobsTask, queueTask).ConfigureAwait(false);

            var ok = storageTask.Result && jobsTask.Result && queueTask.Result;
            var body = new
            {
                storage = storageTask.Result,
                jobs = jobsTask.Result,
                queue = queueTask.Result,
            };
            return ok ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        return app;
    }

    private static class Process
    {
        public static readonly DateTimeOffset StartTime = DateTimeOffset.UtcNow;
    }
}
