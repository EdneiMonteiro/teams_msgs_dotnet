// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// Produtor Service Bus (substitui a Storage Queue). Envia em batches
// (ServiceBusMessageBatch) e usa MessageId determinístico para dedup nativa.

using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeamsMsgs.Shared.Configuration;
using TeamsMsgs.Shared.Queueing;

namespace TeamsMsgs.Shared.Messaging;

public interface ISendQueue
{
    Task EnsureCreatedAsync(CancellationToken ct = default);

    /// <summary>Enfileira um lote em batches do Service Bus. Retorna quantas foram enviadas.</summary>
    Task<int> EnqueueBatchAsync(IReadOnlyList<QueueMessageBody> bodies, CancellationToken ct = default);

    Task EnqueueAsync(QueueMessageBody body, CancellationToken ct = default);

    Task<bool> PingAsync(CancellationToken ct = default);
}

public sealed class ServiceBusSendQueue : ISendQueue
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ServiceBusClient _client;
    private readonly ServiceBusSender _sender;
    private readonly string _queueName;
    private readonly ILogger<ServiceBusSendQueue> _logger;

    public ServiceBusSendQueue(
        ServiceBusClient client,
        IOptions<ServiceBusOptions> options,
        ILogger<ServiceBusSendQueue> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _queueName = options.Value.QueueName;
        _sender = client.CreateSender(_queueName);
        _logger = logger;
    }

    // A fila é provisionada via Bicep; nada a criar no data plane.
    public Task EnsureCreatedAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task<int> EnqueueBatchAsync(IReadOnlyList<QueueMessageBody> bodies, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bodies);
        if (bodies.Count == 0)
        {
            return 0;
        }

        var sent = 0;
        var batch = await _sender.CreateMessageBatchAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var body in bodies)
            {
                var message = BuildMessage(body);
                if (batch.TryAddMessage(message))
                {
                    continue;
                }

                // Batch cheio → flush e abre novo.
                await _sender.SendMessagesAsync(batch, ct).ConfigureAwait(false);
                sent += batch.Count;
                batch.Dispose();
                batch = await _sender.CreateMessageBatchAsync(ct).ConfigureAwait(false);

                if (!batch.TryAddMessage(message))
                {
                    _logger.LogError("Mensagem isolada excede o limite do batch (job={JobId} rowKey={RowKey})",
                        body.JobId, body.RowKey);
                }
            }

            if (batch.Count > 0)
            {
                await _sender.SendMessagesAsync(batch, ct).ConfigureAwait(false);
                sent += batch.Count;
            }
        }
        finally
        {
            batch.Dispose();
        }

        return sent;
    }

    public Task EnqueueAsync(QueueMessageBody body, CancellationToken ct = default)
        => _sender.SendMessageAsync(BuildMessage(body), ct);

    private static ServiceBusMessage BuildMessage(QueueMessageBody body)
    {
        var json = JsonSerializer.Serialize(body, JsonOptions);
        return new ServiceBusMessage(json)
        {
            ContentType = "application/json",
            MessageId = ServiceBusMessageId.Compute(body.JobId, body.RowKey, body.RepeatIndex),
        };
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            await using var receiver = _client.CreateReceiver(_queueName);
            await receiver.PeekMessageAsync(cancellationToken: ct).ConfigureAwait(false);
            return true;
        }
        catch (ServiceBusException)
        {
            return false;
        }
    }
}
