// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// BackgroundService que consome `send-messages` da Storage Queue.
// Replica a lógica de worker/src/worker.ts:
//   1. Receive (até 32 msg) com visibility timeout.
//   2. Para cada msg: parse → resolve message (cache local) → SentMarkStore
//      claim (idempotência) → ContinueConversationAsync → counters.
//   3. Em 403/410: remove ref do Table Storage.
//   4. dequeueCount > MaxDequeueCount → poison queue.
//
// Sem token bucket no código: rate-limit é responsabilidade do sidecar Envoy
// (Istio EnvoyFilter local_ratelimit) no AKS.

using System.Text.Json;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeamsMsgs.Shared.Configuration;
using TeamsMsgs.Shared.Jobs;
using TeamsMsgs.Shared.Queueing;
using TeamsMsgs.Shared.Sending;
using TeamsMsgs.Shared.Storage;

namespace TeamsMsgs.Worker.Hosting;

public sealed class QueueConsumerService : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ISendQueueReceiver _receiver;
    private readonly IJobTracker _jobs;
    private readonly ISentMarkStore _marks;
    private readonly IConversationRefStore _refs;
    private readonly CloudAdapter _adapter;
    private readonly IMemoryCache _cache;
    private readonly WorkerOptions _workerOptions;
    private readonly StorageOptions _storageOptions;
    private readonly BotOptions _botOptions;
    private readonly ILogger<QueueConsumerService> _logger;

    public QueueConsumerService(
        ISendQueueReceiver receiver,
        IJobTracker jobs,
        ISentMarkStore marks,
        IConversationRefStore refs,
        CloudAdapter adapter,
        IMemoryCache cache,
        IOptions<WorkerOptions> workerOptions,
        IOptions<StorageOptions> storageOptions,
        IOptions<BotOptions> botOptions,
        ILogger<QueueConsumerService> logger)
    {
        _receiver = receiver;
        _jobs = jobs;
        _marks = marks;
        _refs = refs;
        _adapter = adapter;
        _cache = cache;
        _workerOptions = workerOptions.Value;
        _storageOptions = storageOptions.Value;
        _botOptions = botOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Worker iniciado. queue={Queue}, maxConcurrent={Max}, maxDequeue={MaxDQ}",
            _storageOptions.QueueName, _workerOptions.MaxConcurrent, _storageOptions.MaxDequeueCount);

        await _receiver.EnsureCreatedAsync(stoppingToken).ConfigureAwait(false);

        using var concurrencyGate = new SemaphoreSlim(_workerOptions.MaxConcurrent);

        while (!stoppingToken.IsCancellationRequested)
        {
            IReadOnlyList<Azure.Storage.Queues.Models.QueueMessage> batch;
            try
            {
                batch = await _receiver
                    .ReceiveAsync(_workerOptions.PollBatchSize, _workerOptions.VisibilityTimeout, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Falha ao receber mensagens da Storage Queue. Tentando novamente em {Delay}.",
                    _workerOptions.EmptyQueueBackoff);
                await Task.Delay(_workerOptions.EmptyQueueBackoff, stoppingToken).ConfigureAwait(false);
                continue;
            }

            if (batch.Count == 0)
            {
                await Task.Delay(_workerOptions.EmptyQueueBackoff, stoppingToken).ConfigureAwait(false);
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

    private async Task ProcessAsync(Azure.Storage.Queues.Models.QueueMessage message, CancellationToken ct)
    {
        if (message.DequeueCount > _storageOptions.MaxDequeueCount)
        {
            _logger.LogWarning(
                "Msg {MessageId} excedeu MaxDequeueCount={Max}. Enviando para poison queue.",
                message.MessageId, _storageOptions.MaxDequeueCount);
            await _receiver.SendToPoisonAsync(message, ct).ConfigureAwait(false);
            return;
        }

        QueueMessageBody? body;
        try
        {
            body = JsonSerializer.Deserialize<QueueMessageBody>(message.MessageText, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Msg {MessageId} JSON inválido. Enviando para poison.", message.MessageId);
            await _receiver.SendToPoisonAsync(message, ct).ConfigureAwait(false);
            return;
        }

        if (body is null || string.IsNullOrEmpty(body.JobId) || string.IsNullOrEmpty(body.RefJson))
        {
            _logger.LogError("Msg {MessageId} sem JobId/RefJson. Enviando para poison.", message.MessageId);
            await _receiver.SendToPoisonAsync(message, ct).ConfigureAwait(false);
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

        // Idempotência: tenta cravar marker antes do envio.
        var claimed = await _marks.TryClaimAsync(body.JobId, body.RowKey, body.RepeatIndex, ct).ConfigureAwait(false);
        if (!claimed)
        {
            _logger.LogDebug("Msg {MessageId} já entregue (idempotente). Completando fila.", message.MessageId);
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

        if (outcome.Permanent)
        {
            await _receiver.CompleteAsync(message, ct).ConfigureAwait(false);
        }
        // Transient → não deleta da fila → Storage Queue redeliveryará após visibility timeout.
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
