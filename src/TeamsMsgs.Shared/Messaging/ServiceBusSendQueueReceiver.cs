// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// Consumidor Service Bus (PeekLock). Substitui o StorageSendQueueReceiver.
// DLQ é nativa do broker (maxDeliveryCount na fila); não há poison queue manual.

using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Options;
using TeamsMsgs.Shared.Configuration;

namespace TeamsMsgs.Shared.Messaging;

public interface ISendQueueReceiver
{
    Task<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveAsync(int maxMessages, TimeSpan maxWait, CancellationToken ct = default);

    Task CompleteAsync(ServiceBusReceivedMessage message, CancellationToken ct = default);

    Task AbandonAsync(ServiceBusReceivedMessage message, CancellationToken ct = default);

    Task DeadLetterAsync(ServiceBusReceivedMessage message, string reason, CancellationToken ct = default);

    Task<bool> PingAsync(CancellationToken ct = default);
}

public sealed class ServiceBusSendQueueReceiver : ISendQueueReceiver, IAsyncDisposable
{
    private readonly ServiceBusReceiver _receiver;

    public ServiceBusSendQueueReceiver(ServiceBusClient client, IOptions<ServiceBusOptions> options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _receiver = client.CreateReceiver(options.Value.QueueName, new ServiceBusReceiverOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            PrefetchCount = 0,
        });
    }

    public async Task<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveAsync(int maxMessages, TimeSpan maxWait, CancellationToken ct = default)
    {
        var capped = Math.Clamp(maxMessages, 1, 100);
        var messages = await _receiver.ReceiveMessagesAsync(capped, maxWait, ct).ConfigureAwait(false);
        return (IReadOnlyList<ServiceBusReceivedMessage>)messages;
    }

    public Task CompleteAsync(ServiceBusReceivedMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _receiver.CompleteMessageAsync(message, ct);
    }

    public Task AbandonAsync(ServiceBusReceivedMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _receiver.AbandonMessageAsync(message, cancellationToken: ct);
    }

    public Task DeadLetterAsync(ServiceBusReceivedMessage message, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _receiver.DeadLetterMessageAsync(message, reason, cancellationToken: ct);
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            await _receiver.PeekMessageAsync(cancellationToken: ct).ConfigureAwait(false);
            return true;
        }
        catch (ServiceBusException)
        {
            return false;
        }
    }

    public ValueTask DisposeAsync() => _receiver.DisposeAsync();
}
