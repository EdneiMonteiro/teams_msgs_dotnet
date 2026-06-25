// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// BackgroundService que consome a fila do Azure Service Bus (PeekLock).
//   1. Receive (até PollBatchSize) com lock do broker.
//   2. Para cada msg: parse → resolve message (cache local + Redis) →
//      rate limit (token bucket Redis) → ContinueConversation → counters.
//   3. Em 403/410: remove ref do Table Storage.
//   4. Falha transitória → Abandon (redelivery; o broker dead-letters ao
//      exceder MaxDeliveryCount). Idempotência é nativa (MessageId determinístico).

using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeamsMsgs.Shared.Configuration;
using TeamsMsgs.Shared.Jobs;
using TeamsMsgs.Shared.Messaging;
using TeamsMsgs.Shared.Queueing;
using TeamsMsgs.Shared.RateLimiting;
using TeamsMsgs.Shared.Sending;
using TeamsMsgs.Shared.Storage;

namespace TeamsMsgs.Worker.Hosting;

public sealed class QueueConsumerService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ReceiveMaxWait = TimeSpan.FromSeconds(5);

    private readonly ISendQueueReceiver _receiver;
    private readonly IJobTracker _jobs;
    private readonly IConversationRefStore _refs;
    private readonly IRateLimiter _rateLimiter;
    private readonly CloudAdapter _adapter;
    private readonly IMemoryCache _cache;
    private readonly WorkerOptions _workerOptions;
    private readonly ServiceBusOptions _serviceBusOptions;
    private readonly RateLimitOptions _rateLimitOptions;
    private readonly BotOptions _botOptions;
    private readonly ILogger<QueueConsumerService> _logger;

    public QueueConsumerService(
        ISendQueueReceiver receiver,
        IJobTracker jobs,
        IConversationRefStore refs,
        IRateLimiter rateLimiter,
        CloudAdapter adapter,
        IMemoryCache cache,
        IOptions<WorkerOptions> workerOptions,
        IOptions<ServiceBusOptions> serviceBusOptions,
        IOptions<RateLimitOptions> rateLimitOptions,
        IOptions<BotOptions> botOptions,
        ILogger<QueueConsumerService> logger)
    {
        _receiver = receiver;
        _jobs = jobs;
        _refs = refs;
        _rateLimiter = rateLimiter;
        _adapter = adapter;
        _cache = cache;
        _workerOptions = workerOptions.Value;
        _serviceBusOptions = serviceBusOptions.Value;
        _rateLimitOptions = rateLimitOptions.Value;
        _botOptions = botOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Worker iniciado. queue={Queue}, maxConcurrent={Max}, maxDelivery={MaxDC}",
            _serviceBusOptions.QueueName, _workerOptions.MaxConcurrent, _serviceBusOptions.MaxDeliveryCount);

        using var concurrencyGate = new SemaphoreSlim(_workerOptions.MaxConcurrent);

        while (!stoppingToken.IsCancellationRequested)
        {
            IReadOnlyList<ServiceBusReceivedMessage> batch;
            try
            {
                batch = await _receiver
                    .ReceiveAsync(_workerOptions.PollBatchSize, ReceiveMaxWait, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Falha ao receber do Service Bus. Tentando novamente em {Delay}.",
                    _workerOptions.EmptyQueueBackoff);
                await Task.Delay(_workerOptions.EmptyQueueBackoff, stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (batch.Count == 0)
            {
                continue;
            }

            var tasks = new List<Task>(batch.Count);
            foreach (var message in batch)
            {
                await concurrencyGate.WaitAsync(stoppingToken).ConfigureAwait(false);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await ProcessAsync(message, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Erro inesperado processando msg {MessageId}", message.MessageId);
                        try
                        {
                            await _receiver.AbandonAsync(message, stoppingToken).ConfigureAwait(false);
                        }
                        catch (ServiceBusException)
                        {
                            // lock perdido/expirado — o broker reentrega.
                        }
                    }
                    finally
                    {
                        concurrencyGate.Release();
                    }
                }, stoppingToken));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        _logger.LogInformation("Worker encerrando.");
    }

    private async Task ProcessAsync(ServiceBusReceivedMessage message, CancellationToken ct)
    {
        QueueMessageBody? body;
        try
        {
            body = JsonSerializer.Deserialize<QueueMessageBody>(message.Body.ToString(), JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Msg {MessageId} JSON inválido. Dead-letter.", message.MessageId);
            await _receiver.DeadLetterAsync(message, "InvalidJson", ct).ConfigureAwait(false);
            return;
        }

        if (body is null || string.IsNullOrEmpty(body.JobId) || string.IsNullOrEmpty(body.RefJson))
        {
            _logger.LogError("Msg {MessageId} sem JobId/RefJson. Dead-letter.", message.MessageId);
            await _receiver.DeadLetterAsync(message, "MissingFields", ct).ConfigureAwait(false);
            return;
        }

        var resolved = await ResolveMessageAsync(body.JobId, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            _logger.LogError("Job {JobId} não encontrado (expirado?). Drop msg {MessageId}.", body.JobId, message.MessageId);
            await _jobs.IncrementFailedAsync(body.JobId, "Job não encontrado", ct).ConfigureAwait(false);
            await _receiver.CompleteAsync(message, ct).ConfigureAwait(false);
            return;
        }

        ConversationReference reference;
        try
        {
            reference = JsonSerializer.Deserialize<ConversationReference>(body.RefJson, JsonOptions)
                ?? throw new InvalidOperationException("ref desserializou como null");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _logger.LogError(ex, "RefJson inválido em job {JobId}. Marcando como failed.", body.JobId);
            await _jobs.IncrementFailedAsync(body.JobId, $"refJson inválido: {ex.Message}", ct).ConfigureAwait(false);
            await _receiver.CompleteAsync(message, ct).ConfigureAwait(false);
            return;
        }

        // Rate limit global (token bucket Redis) antes do envio ao Bot Framework.
        if (_rateLimitOptions.Enabled)
        {
            await _rateLimiter.AcquireAsync(ct).ConfigureAwait(false);
        }

        var outcome = await SendWithRetry.ExecuteAsync(
            send: async token =>
            {
                await _adapter.ContinueConversationAsync(
                    _botOptions.AppId,
                    reference,
                    async (turn, innerCt) => await DeliverAsync(turn, resolved, innerCt).ConfigureAwait(false),
                    token).ConfigureAwait(false);
            },
            extractStatus: BotHttpStatus.ExtractStatus,
            extractRetryAfter: BotHttpStatus.ExtractRetryAfter,
            ct: ct).ConfigureAwait(false);

        if (outcome.Ok)
        {
            await _jobs.IncrementSentAsync(body.JobId, ct).ConfigureAwait(false);
            await _receiver.CompleteAsync(message, ct).ConfigureAwait(false);
            return;
        }

        if (outcome.Permanent)
        {
            if (outcome.StatusCode is 403 or 410)
            {
                try
                {
                    await _refs.RemoveByRowKeyAsync(body.RowKey, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Falha ao remover ref {RowKey}", body.RowKey);
                }
            }

            await _jobs.IncrementFailedAsync(body.JobId, outcome.ErrorMsg, ct).ConfigureAwait(false);
            _logger.LogWarning("❌ Job {JobId} msg {MessageId}: {Status} {Error}",
                body.JobId, message.MessageId, outcome.StatusCode, outcome.ErrorMsg);
            await _receiver.CompleteAsync(message, ct).ConfigureAwait(false);
            return;
        }

        // Transitória: abandona → broker reentrega; ao exceder MaxDeliveryCount vai p/ DLQ.
        _logger.LogWarning("⚠️ Job {JobId} msg {MessageId} transitória: {Status} {Error}. Abandon.",
            body.JobId, message.MessageId, outcome.StatusCode, outcome.ErrorMsg);
        await _receiver.AbandonAsync(message, ct).ConfigureAwait(false);
    }

    private static Task DeliverAsync(ITurnContext turn, ResolvedMessage resolved, CancellationToken ct)
    {
        if (resolved.Type == TeamsMsgs.Shared.Validation.MessageType.Text)
        {
            return turn.SendActivityAsync(resolved.Serialized, cancellationToken: ct);
        }

        using var doc = JsonDocument.Parse(resolved.Serialized);
        if (!doc.RootElement.TryGetProperty("content", out var content))
        {
            throw new InvalidOperationException("AdaptiveCard sem 'content'");
        }

        var card = new Attachment
        {
            ContentType = "application/vnd.microsoft.card.adaptive",
            Content = JsonSerializer.Deserialize<object>(content.GetRawText(), JsonOptions),
        };
        return turn.SendActivityAsync(MessageFactory.Attachment(card), ct);
    }

    private async Task<ResolvedMessage?> ResolveMessageAsync(string jobId, CancellationToken ct)
    {
        if (_cache.TryGetValue<ResolvedMessage>(jobId, out var cached) && cached is not null)
        {
            return cached;
        }
        var msg = await _jobs.GetMessageAsync(jobId, ct).ConfigureAwait(false);
        if (msg is not null)
        {
            _cache.Set(jobId, msg, _workerOptions.MessageCacheTtl);
        }
        return msg;
    }
}
