// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// Persistência durável dos conversationReferences em Azure Table Storage.

using System.Runtime.CompilerServices;
using System.Text;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TeamsMsgs.Shared.Azure;
using TeamsMsgs.Shared.Configuration;
using TeamsMsgs.Shared.Redis;

namespace TeamsMsgs.Shared.Storage;

public sealed record ConversationRef(string RowKey, string RefJson);

public interface IConversationRefStore
{
    Task EnsureCreatedAsync(CancellationToken ct = default);

    Task SaveAsync(string conversationId, string refJson, CancellationToken ct = default);

    Task RemoveAsync(string conversationId, CancellationToken ct = default);

    Task RemoveByRowKeyAsync(string rowKey, CancellationToken ct = default);

    IAsyncEnumerable<ConversationRef> StreamAsync(CancellationToken ct = default);

    Task<long> CountAsync(CancellationToken ct = default);

    Task<bool> PingAsync(CancellationToken ct = default);
}

public sealed class ConversationRefStore : IConversationRefStore
{
    private const string PartitionKey = "refs";
    private const string RefsSet = "refs:active";

    private readonly TableClient _table;
    private readonly IRedisConnection _redis;
    private readonly ILogger<ConversationRefStore> _logger;
    private bool _ensured;

    public ConversationRefStore(
        AzureClientFactory factory,
        IRedisConnection redis,
        IOptions<StorageOptions> options,
        ILogger<ConversationRefStore> logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(options);
        _table = factory.CreateTableClient(options.Value.RefsTableName);
        _redis = redis;
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
            await _table.CreateIfNotExistsAsync(ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex)
        {
            _logger.LogWarning(ex, "Falha ao garantir tabela {Table}", _table.Name);
        }
        _ensured = true;
    }

    internal static string ToSafeRowKey(string conversationId)
    {
        var bytes = Encoding.UTF8.GetBytes(conversationId);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public async Task SaveAsync(string conversationId, string refJson, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        var rowKey = ToSafeRowKey(conversationId);
        var entity = new TableEntity(PartitionKey, rowKey)
        {
            ["refJson"] = refJson,
        };
        await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct).ConfigureAwait(false);
        await TryUpdateIndexAsync(db => db.SetAddAsync(RefsSet, rowKey), rowKey, "SADD").ConfigureAwait(false);
    }

    public async Task RemoveAsync(string conversationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        await RemoveByRowKeyAsync(ToSafeRowKey(conversationId), ct).ConfigureAwait(false);
    }

    public async Task RemoveByRowKeyAsync(string rowKey, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        try
        {
            await _table.DeleteEntityAsync(PartitionKey, rowKey, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // já removida
        }
        await TryUpdateIndexAsync(db => db.SetRemoveAsync(RefsSet, rowKey), rowKey, "SREM").ConfigureAwait(false);
    }

    // O índice Redis é cache de leitura; falhas não são fatais (Table é a fonte da verdade).
    private async Task TryUpdateIndexAsync(Func<IDatabase, Task> op, string rowKey, string label)
    {
        try
        {
            await op(_redis.Database).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Índice Redis {Op} falhou (não-fatal) p/ {RowKey}", label, rowKey);
        }
    }

    public async IAsyncEnumerable<ConversationRef> StreamAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {PartitionKey}");
        await foreach (var entity in _table.QueryAsync<TableEntity>(filter, cancellationToken: ct).ConfigureAwait(false))
        {
            var json = entity.GetString("refJson") ?? string.Empty;
            yield return new ConversationRef(entity.RowKey, json);
        }
    }

    public async Task<long> CountAsync(CancellationToken ct = default)
    {
        // Caminho rápido O(1): SCARD do índice Redis. Fallback p/ varredura da Table.
        try
        {
            var card = await _redis.Database.SetLengthAsync(RefsSet).ConfigureAwait(false);
            if (card > 0)
            {
                return card;
            }
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "SCARD falhou; usando varredura da Table como fallback.");
        }

        long count = 0;
        await foreach (var _ in StreamAsync(ct).ConfigureAwait(false))
        {
            count++;
        }
        return count;
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
