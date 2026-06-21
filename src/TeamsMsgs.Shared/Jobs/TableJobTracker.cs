// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// Substituto do Redis para counters de job. Atualização via optimistic
// concurrency (ETag/If-Match) com retry exponencial em 412.

using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using TeamsMsgs.Shared.Azure;
using TeamsMsgs.Shared.Configuration;
using TeamsMsgs.Shared.Validation;

namespace TeamsMsgs.Shared.Jobs;

public sealed record ResolvedMessage(MessageType Type, string Serialized);

public sealed record JobStatus(
    string JobId,
    string Message,
    MessageType MessageType,
    long Total,
    long Sent,
    long Failed,
    string Status,
    int Progress,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> Errors);

public interface IJobTracker
{
    Task CreateAsync(string jobId, string message, MessageType messageType, long total, CancellationToken ct = default);

    Task SetStatusAsync(string jobId, string status, CancellationToken ct = default);

    Task UpdateTotalAsync(string jobId, long total, CancellationToken ct = default);

    Task IncrementSentAsync(string jobId, CancellationToken ct = default);

    Task IncrementFailedAsync(string jobId, string? error, CancellationToken ct = default);

    Task<JobStatus?> GetAsync(string jobId, CancellationToken ct = default);

    Task<ResolvedMessage?> GetMessageAsync(string jobId, CancellationToken ct = default);

    Task<bool> PingAsync(CancellationToken ct = default);
}

public sealed class TableJobTracker : IJobTracker
{
    private const string PartitionKey = "jobs";
    private const int MaxErrors = 50;
    // Counter sharding: distribui sent/failed em N sub-entidades (jobId_sN) para
    // eliminar a contenção de ETag no registro único do job. GetAsync soma os shards.
    private const int ShardCount = 16;

    private readonly TableClient _table;
    private readonly ILogger<TableJobTracker> _logger;
    private readonly ResiliencePipeline _retry;
    private bool _ensured;

    public TableJobTracker(
        AzureClientFactory factory,
        IOptions<StorageOptions> options,
        ILogger<TableJobTracker> logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        _table = factory.CreateTableClient(options.Value.JobsTableName);
        _logger = logger;

        // O counter de job é uma entidade "quente": sob alta concorrência (muitos
        // workers KEDA incrementando o mesmo registro), o conflito de ETag (412) é
        // frequente. Budget de retry generoso garante convergência (cada tentativa
        // é um read+write barato) sem estourar o visibility timeout da fila (5min).
        _retry = new ResiliencePipelineBuilder()
            .AddRetry(new Polly.Retry.RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<RequestFailedException>(static ex =>
                    ex.Status is 412 or 409 or 408 or 429 or 500 or 502 or 503 or 504),
                MaxRetryAttempts = 50,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(50),
                MaxDelay = TimeSpan.FromSeconds(2),
                UseJitter = true,
            })
            .Build();
    }

    private async Task EnsureAsync(CancellationToken ct)
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

    public async Task CreateAsync(string jobId, string message, MessageType messageType, long total, CancellationToken ct = default)
    {
        await EnsureAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var entity = new TableEntity(PartitionKey, jobId)
        {
            ["total"] = total,
            ["sent"] = 0L,
            ["failed"] = 0L,
            ["status"] = "queued",
            ["messageType"] = messageType.ToString().ToLowerInvariant(),
            ["message"] = message,
            ["errors"] = "[]",
            ["createdAt"] = now,
            ["updatedAt"] = now,
        };
        await _table.AddEntityAsync(entity, ct).ConfigureAwait(false);
    }

    public Task SetStatusAsync(string jobId, string status, CancellationToken ct = default)
        => UpdateAsync(jobId, e =>
        {
            e["status"] = status;
            e["updatedAt"] = DateTimeOffset.UtcNow;
        }, ct);

    public Task UpdateTotalAsync(string jobId, long total, CancellationToken ct = default)
        => UpdateAsync(jobId, e =>
        {
            e["total"] = total;
            e["updatedAt"] = DateTimeOffset.UtcNow;
        }, ct);

    public Task IncrementSentAsync(string jobId, CancellationToken ct = default)
        => IncrementShardAsync(jobId, "sent", ct);

    public async Task IncrementFailedAsync(string jobId, string? error, CancellationToken ct = default)
    {
        await IncrementShardAsync(jobId, "failed", ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(error))
        {
            await AppendErrorAsync(jobId, error!, ct).ConfigureAwait(false);
        }
    }

    // Incrementa um shard aleatório (jobId_sN). A contenção por shard é ~ShardCount
    // menor que no registro único, então o retry de ETag quase nunca é exercido.
    private async Task IncrementShardAsync(string jobId, string field, CancellationToken ct)
    {
        await EnsureAsync(ct).ConfigureAwait(false);
        var rowKey = $"{jobId}_s{Random.Shared.Next(ShardCount)}";
        await _retry.ExecuteAsync(async token =>
        {
            try
            {
                var resp = await _table.GetEntityAsync<TableEntity>(PartitionKey, rowKey, cancellationToken: token).ConfigureAwait(false);
                var entity = resp.Value;
                entity[field] = (entity.GetInt64(field) ?? 0) + 1;
                await _table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Merge, token).ConfigureAwait(false);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                await _table.AddEntityAsync(new TableEntity(PartitionKey, rowKey) { [field] = 1L }, token).ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
    }

    // Erros ficam no registro principal, limitados a MaxErrors; a escrita cessa após o cap
    // (bounded), então não vira gargalo como os contadores.
    private async Task AppendErrorAsync(string jobId, string error, CancellationToken ct)
    {
        await _retry.ExecuteAsync(async token =>
        {
            var resp = await _table.GetEntityAsync<TableEntity>(PartitionKey, jobId, cancellationToken: token).ConfigureAwait(false);
            var entity = resp.Value;
            var list = JsonSerializer.Deserialize<List<string>>(entity.GetString("errors") ?? "[]") ?? new List<string>();
            if (list.Count >= MaxErrors)
            {
                return;
            }
            list.Add(error);
            entity["errors"] = JsonSerializer.Serialize(list);
            entity["updatedAt"] = DateTimeOffset.UtcNow;
            await _table.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Merge, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<JobStatus?> GetAsync(string jobId, CancellationToken ct = default)
    {
        await EnsureAsync(ct).ConfigureAwait(false);
        TableEntity e;
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(PartitionKey, jobId, cancellationToken: ct).ConfigureAwait(false);
            e = response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }

        var (sent, failed) = await SumShardsAsync(jobId, ct).ConfigureAwait(false);
        var total = e.GetInt64("total") ?? 0;
        var typeStr = e.GetString("messageType") ?? "text";
        var type = string.Equals(typeStr, "card", StringComparison.OrdinalIgnoreCase) ? MessageType.Card : MessageType.Text;
        var errorsJson = e.GetString("errors") ?? "[]";
        var errors = JsonSerializer.Deserialize<List<string>>(errorsJson) ?? new List<string>();

        // Status "completed" é derivado da soma dos shards vs total (evita "completed"
        // prematuro enquanto total=0 durante o enqueue).
        var stored = e.GetString("status") ?? "unknown";
        var status = (stored != "failed" && total > 0 && (sent + failed) >= total) ? "completed" : stored;

        return new JobStatus(
            jobId,
            e.GetString("message") ?? string.Empty,
            type,
            total,
            sent,
            failed,
            status,
            total > 0 ? (int)Math.Round(((double)(sent + failed) / total) * 100) : 0,
            e.GetDateTimeOffset("createdAt") ?? default,
            e.GetDateTimeOffset("updatedAt") ?? default,
            errors);
    }

    private async Task<(long Sent, long Failed)> SumShardsAsync(string jobId, CancellationToken ct)
    {
        var reads = new Task<(long Sent, long Failed)>[ShardCount];
        for (var i = 0; i < ShardCount; i++)
        {
            reads[i] = ReadShardAsync($"{jobId}_s{i}", ct);
        }
        var results = await Task.WhenAll(reads).ConfigureAwait(false);
        long sent = 0, failed = 0;
        foreach (var (s, f) in results)
        {
            sent += s;
            failed += f;
        }
        return (sent, failed);
    }

    private async Task<(long Sent, long Failed)> ReadShardAsync(string rowKey, CancellationToken ct)
    {
        try
        {
            var r = await _table.GetEntityAsync<TableEntity>(PartitionKey, rowKey, cancellationToken: ct).ConfigureAwait(false);
            return (r.Value.GetInt64("sent") ?? 0, r.Value.GetInt64("failed") ?? 0);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return (0, 0);
        }
    }

    public async Task<ResolvedMessage?> GetMessageAsync(string jobId, CancellationToken ct = default)
    {
        await EnsureAsync(ct).ConfigureAwait(false);
        try
        {
            var response = await _table
                .GetEntityAsync<TableEntity>(PartitionKey, jobId, new[] { "message", "messageType" }, ct)
                .ConfigureAwait(false);
            var e = response.Value;
            var typeStr = e.GetString("messageType") ?? "text";
            var type = string.Equals(typeStr, "card", StringComparison.OrdinalIgnoreCase) ? MessageType.Card : MessageType.Text;
            return new ResolvedMessage(type, e.GetString("message") ?? string.Empty);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            await EnsureAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException)
        {
            return false;
        }
    }

    private async Task UpdateAsync(string jobId, Action<TableEntity> mutate, CancellationToken ct)
    {
        await EnsureAsync(ct).ConfigureAwait(false);

        await _retry.ExecuteAsync(async token =>
        {
            var response = await _table
                .GetEntityAsync<TableEntity>(PartitionKey, jobId, cancellationToken: token)
                .ConfigureAwait(false);
            var entity = response.Value;
            mutate(entity);
            await _table
                .UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace, token)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
