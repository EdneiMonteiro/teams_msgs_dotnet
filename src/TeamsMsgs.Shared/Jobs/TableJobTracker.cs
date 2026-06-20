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
        => UpdateAsync(jobId, e =>
        {
            var sent = e.GetInt64("sent") ?? 0;
            var failed = e.GetInt64("failed") ?? 0;
            var total = e.GetInt64("total") ?? 0;
            e["sent"] = sent + 1;
            e["status"] = ((sent + 1) + failed) >= total ? "completed" : "processing";
            e["updatedAt"] = DateTimeOffset.UtcNow;
        }, ct);

    public Task IncrementFailedAsync(string jobId, string? error, CancellationToken ct = default)
        => UpdateAsync(jobId, e =>
        {
            var sent = e.GetInt64("sent") ?? 0;
            var failed = e.GetInt64("failed") ?? 0;
            var total = e.GetInt64("total") ?? 0;
            failed += 1;
            e["failed"] = failed;

            if (!string.IsNullOrWhiteSpace(error))
            {
                var json = e.GetString("errors") ?? "[]";
                var list = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
                if (list.Count < MaxErrors)
                {
                    list.Add(error!);
                    e["errors"] = JsonSerializer.Serialize(list);
                }
            }

            e["status"] = (sent + failed) >= total ? "completed" : "processing";
            e["updatedAt"] = DateTimeOffset.UtcNow;
        }, ct);

    public async Task<JobStatus?> GetAsync(string jobId, CancellationToken ct = default)
    {
        await EnsureAsync(ct).ConfigureAwait(false);
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(PartitionKey, jobId, cancellationToken: ct).ConfigureAwait(false);
            var e = response.Value;
            var total = e.GetInt64("total") ?? 0;
            var sent = e.GetInt64("sent") ?? 0;
            var failed = e.GetInt64("failed") ?? 0;
            var typeStr = e.GetString("messageType") ?? "text";
            var type = string.Equals(typeStr, "card", StringComparison.OrdinalIgnoreCase) ? MessageType.Card : MessageType.Text;
            var errorsJson = e.GetString("errors") ?? "[]";
            var errors = JsonSerializer.Deserialize<List<string>>(errorsJson) ?? new List<string>();
            return new JobStatus(
                jobId,
                e.GetString("message") ?? string.Empty,
                type,
                total,
                sent,
                failed,
                e.GetString("status") ?? "unknown",
                total > 0 ? (int)Math.Round(((double)(sent + failed) / total) * 100) : 0,
                e.GetDateTimeOffset("createdAt") ?? default,
                e.GetDateTimeOffset("updatedAt") ?? default,
                errors);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
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
