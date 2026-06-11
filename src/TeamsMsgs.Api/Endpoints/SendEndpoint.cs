// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// Port de src/index.ts:/api/send.
// Streaming refs do Table Storage → enqueue na Storage Queue com paralelismo
// controlado (SendFlushConcurrency = nº de SendMessageAsync em vôo).
//
// Diferenças vs versão TS/Service Bus:
//   - Sem batches nativos. Cada msg = 1 SendMessageAsync. Paralelismo via
//     SemaphoreSlim para evitar saturar o pod.
//   - 'enqueued' agora é igual a refsSeen * repeatCount - drops.
//   - 'drops' permanece 0 nesta PoC (Storage Queue só limita o tamanho da
//     msg individual a 64KB; AdaptiveCards >64KB ficam fora de escopo).

using System.Text.Json;
using Microsoft.Extensions.Options;
using TeamsMsgs.Shared.Configuration;
using TeamsMsgs.Shared.Jobs;
using TeamsMsgs.Shared.Queueing;
using TeamsMsgs.Shared.Storage;
using TeamsMsgs.Shared.Validation;

namespace TeamsMsgs.Api.Endpoints;

public static class SendEndpoint
{
    public static IEndpointRouteBuilder MapSendEndpoint(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/send", HandleAsync);

        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext httpContext,
        IConversationRefStore refs,
        IJobTracker jobs,
        ISendQueue queue,
        IOptions<ApiOptions> apiOptions,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("SendEndpoint");
        var concurrency = apiOptions.Value.SendFlushConcurrency;

        SendRequestBody? body;
        try
        {
            body = await httpContext.Request.ReadFromJsonAsync<SendRequestBody>(ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            return Results.BadRequest(new { error = $"Corpo JSON inválido: {ex.Message}" });
        }

        var validation = MessageValidator.Validate(body?.Message);
        if (validation is MessageValidationResult.Failure fail)
        {
            return Results.BadRequest(new { error = fail.Error });
        }
        var validated = ((MessageValidationResult.Success)validation).Message;

        var repeat = Math.Clamp(body?.Repeat ?? 1, 1, 100_000);

        await refs.EnsureCreatedAsync(ct).ConfigureAwait(false);
        await queue.EnsureCreatedAsync(ct).ConfigureAwait(false);

        // Pré-checagem: existe pelo menos 1 ref?
        var hasAny = false;
        await foreach (var _ in refs.StreamAsync(ct).ConfigureAwait(false))
        {
            hasAny = true;
            break;
        }
        if (!hasAny)
        {
            return Results.Json(
                new { error = "Nenhum usuário registrado. Instale o Teams app primeiro." },
                statusCode: StatusCodes.Status409Conflict);
        }

        var jobId = Guid.NewGuid().ToString("N");

        // Cria job com total provisório; ajusta depois com a contagem real.
        await jobs.CreateAsync(jobId, validated.Serialized, validated.Type, 0L, ct).ConfigureAwait(false);

        using var semaphore = new SemaphoreSlim(concurrency);
        var inFlight = new List<Task>();
        long refsSeen = 0;
        long enqueued = 0;

        try
        {
            await foreach (var refEntity in refs.StreamAsync(ct).ConfigureAwait(false))
            {
                refsSeen++;
                for (var r = 0; r < repeat; r++)
                {
                    await semaphore.WaitAsync(ct).ConfigureAwait(false);
                    var msg = new QueueMessageBody(jobId, refEntity.RefJson, refEntity.RowKey, r);
                    var task = Task.Run(async () =>
                    {
                        try
                        {
                            await queue.EnqueueAsync(msg, ct).ConfigureAwait(false);
                            Interlocked.Increment(ref enqueued);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, ct);
                    inFlight.Add(task);
                }
            }

            await Task.WhenAll(inFlight).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Falha durante fan-out do job {JobId}", jobId);
            await jobs.SetStatusAsync(jobId, "failed", ct).ConfigureAwait(false);
            return Results.Json(
                new { error = $"Falha ao enfileirar mensagens: {ex.Message}" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var total = refsSeen * repeat;
        await jobs.UpdateTotalAsync(jobId, total, ct).ConfigureAwait(false);
        await jobs.SetStatusAsync(jobId, "processing", ct).ConfigureAwait(false);

        logger.LogInformation(
            "Job {JobId}: {Enqueued} enfileiradas (refs={Refs} repeat={Repeat})",
            jobId, enqueued, refsSeen, repeat);

        return Results.Accepted(value: new
        {
            jobId,
            refs = refsSeen,
            repeat,
            total,
            enqueued,
            drops = 0,
            messageType = validated.Type.ToString().ToLowerInvariant(),
            status = "queued",
            statusUrl = $"/api/jobs/{jobId}",
        });
    }

    private sealed class SendRequestBody
    {
        public JsonElement? Message { get; set; }

        public int? Repeat { get; set; }
    }
}
