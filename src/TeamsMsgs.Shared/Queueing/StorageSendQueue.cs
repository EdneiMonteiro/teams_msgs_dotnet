// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// Wrapper sobre Azure.Storage.Queues. Diferenças vs Service Bus:
//   - Sem batch nativo (cada msg = 1 chamada HTTP).
//   - Sem DLQ built-in (poison queue manual).
//   - Sem dedup nativa (idempotência em ISentMarkStore).

using System.Text.Json;
using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeamsMsgs.Shared.Azure;
using TeamsMsgs.Shared.Configuration;

namespace TeamsMsgs.Shared.Queueing;

public interface ISendQueue
{
    Task EnsureCreatedAsync(CancellationToken ct = default);

    Task EnqueueAsync(QueueMessageBody body, CancellationToken ct = default);

    Task<bool> PingAsync(CancellationToken ct = default);
}

public sealed class StorageSendQueue : ISendQueue
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly QueueClient _queue;
    private readonly ILogger<StorageSendQueue> _logger;
    private bool _ensured;

    public StorageSendQueue(
        AzureClientFactory factory,
        IOptions<StorageOptions> options,
        ILogger<StorageSendQueue> logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        _queue = factory.CreateQueueClient(options.Value.QueueName);
        _logger = logger;
    }

    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        if (_ensured)
        {
            return;
        }
        try
        {
            await _queue.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex)
        {
            _logger.LogWarning(ex, "Falha ao garantir fila {Queue}", _queue.Name);
        }
        _ensured = true;
    }

    public async Task EnqueueAsync(QueueMessageBody body, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(body, JsonOptions);
        await _queue.SendMessageAsync(json, ct).ConfigureAwait(false);
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            await EnsureCreatedAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException)
        {
            return false;
        }
    }
}

public interface ISendQueueReceiver
{
    Task EnsureCreatedAsync(CancellationToken ct = default);

    Task<IReadOnlyList<QueueMessage>> ReceiveAsync(int maxMessages, TimeSpan visibility, CancellationToken ct = default);

    Task CompleteAsync(QueueMessage message, CancellationToken ct = default);

    Task SendToPoisonAsync(QueueMessage message, CancellationToken ct = default);
}

public sealed class StorageSendQueueReceiver : ISendQueueReceiver
{
    private readonly QueueClient _queue;
    private readonly QueueClient _poison;
    private readonly ILogger<StorageSendQueueReceiver> _logger;
    private bool _ensured;

    public StorageSendQueueReceiver(
        AzureClientFactory factory,
        IOptions<StorageOptions> options,
        ILogger<StorageSendQueueReceiver> logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        var opts = options.Value;
        _queue = factory.CreateQueueClient(opts.QueueName);
        _poison = factory.CreateQueueClient(opts.PoisonQueueName);
        _logger = logger;
    }

    public async Task EnsureCreatedAsync(CancellationToken ct = default)
    {
        if (_ensured)
        {
            return;
        }
        try
        {
            await _queue.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);
            await _poison.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex)
        {
            _logger.LogWarning(ex, "Falha ao garantir filas {Queue}/{Poison}", _queue.Name, _poison.Name);
        }
        _ensured = true;
    }

    public async Task<IReadOnlyList<QueueMessage>> ReceiveAsync(int maxMessages, TimeSpan visibility, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        var capped = Math.Clamp(maxMessages, 1, 32);
        var response = await _queue.ReceiveMessagesAsync(capped, visibility, ct).ConfigureAwait(false);
        return response.Value ?? Array.Empty<QueueMessage>();
    }

    public async Task CompleteAsync(QueueMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await _queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, ct).ConfigureAwait(false);
    }

    public async Task SendToPoisonAsync(QueueMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        await _poison.SendMessageAsync(message.MessageText, ct).ConfigureAwait(false);
        await _queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, ct).ConfigureAwait(false);
    }
}
