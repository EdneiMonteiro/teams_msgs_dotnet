// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// Job tracker em Redis (substitui o TableJobTracker). Espelha redis-tracker.ts:
// hash `job:{id}` com counters atômicos (HINCRBY), payload da mensagem e TTL 24h.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TeamsMsgs.Shared.Redis;
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

public sealed class RedisJobTracker : IJobTracker
{
    private const int MaxErrors = 50;
    private static readonly TimeSpan JobTtl = TimeSpan.FromHours(24);

    private readonly IRedisConnection _redis;
    private readonly ILogger<RedisJobTracker> _logger;

    public RedisJobTracker(IRedisConnection redis, ILogger<RedisJobTracker> logger)
    {
        ArgumentNullException.ThrowIfNull(redis);
        _redis = redis;
        _logger = logger;
    }

    private static string Key(string jobId) => $"job:{jobId}";

    private IDatabase Db => _redis.Database;

    public async Task CreateAsync(string jobId, string message, MessageType messageType, long total, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var key = Key(jobId);
        await Db.HashSetAsync(key, new HashEntry[]
        {
            new("message", message),
            new("messageType", messageType.ToString().ToLowerInvariant()),
            new("total", total),
            new("sent", 0),
            new("failed", 0),
            new("status", "queued"),
            new("createdAt", now),
            new("updatedAt", now),
            new("errors", "[]"),
        }).ConfigureAwait(false);
        await Db.KeyExpireAsync(key, JobTtl).ConfigureAwait(false);
    }

    public Task SetStatusAsync(string jobId, string status, CancellationToken ct = default)
        => Db.HashSetAsync(Key(jobId), new HashEntry[]
        {
            new("status", status),
            new("updatedAt", DateTimeOffset.UtcNow.ToString("O")),
        });

    public Task UpdateTotalAsync(string jobId, long total, CancellationToken ct = default)
        => Db.HashSetAsync(Key(jobId), new HashEntry[]
        {
            new("total", total),
            new("updatedAt", DateTimeOffset.UtcNow.ToString("O")),
        });

    public async Task IncrementSentAsync(string jobId, CancellationToken ct = default)
    {
        var key = Key(jobId);
        var sent = await Db.HashIncrementAsync(key, "sent", 1).ConfigureAwait(false);
        await ReconcileStatusAsync(key, sent, null).ConfigureAwait(false);
    }

    public async Task IncrementFailedAsync(string jobId, string? error, CancellationToken ct = default)
    {
        var key = Key(jobId);
        var failed = await Db.HashIncrementAsync(key, "failed", 1).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(error))
        {
            var json = (string?)await Db.HashGetAsync(key, "errors").ConfigureAwait(false) ?? "[]";
            var list = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            if (list.Count < MaxErrors)
            {
                list.Add(error!);
                await Db.HashSetAsync(key, "errors", JsonSerializer.Serialize(list)).ConfigureAwait(false);
            }
        }

        await ReconcileStatusAsync(key, null, failed).ConfigureAwait(false);
    }

    private async Task ReconcileStatusAsync(string key, long? sent, long? failed)
    {
        var values = await Db.HashGetAsync(key, new RedisValue[] { "sent", "failed", "total" }).ConfigureAwait(false);
        var s = sent ?? (long)values[0];
        var f = failed ?? (long)values[1];
        var total = (long)values[2];
        var status = total > 0 && (s + f) >= total ? "completed" : "processing";
        await Db.HashSetAsync(key, new HashEntry[]
        {
            new("status", status),
            new("updatedAt", DateTimeOffset.UtcNow.ToString("O")),
        }).ConfigureAwait(false);
    }

    public async Task<JobStatus?> GetAsync(string jobId, CancellationToken ct = default)
    {
        var entries = await Db.HashGetAllAsync(Key(jobId)).ConfigureAwait(false);
        if (entries.Length == 0)
        {
            return null;
        }

        var map = entries.ToDictionary(e => (string)e.Name!, e => e.Value);
        string Get(string k) => map.TryGetValue(k, out var v) ? (string?)v ?? string.Empty : string.Empty;
        long GetLong(string k) => map.TryGetValue(k, out var v) && v.TryParse(out long l) ? l : 0;

        var total = GetLong("total");
        var sent = GetLong("sent");
        var failed = GetLong("failed");
        var type = string.Equals(Get("messageType"), "card", StringComparison.OrdinalIgnoreCase)
            ? MessageType.Card : MessageType.Text;
        var errors = JsonSerializer.Deserialize<List<string>>(Get("errors") is { Length: > 0 } ej ? ej : "[]")
            ?? new List<string>();

        return new JobStatus(
            jobId,
            Get("message"),
            type,
            total,
            sent,
            failed,
            Get("status") is { Length: > 0 } st ? st : "unknown",
            total > 0 ? (int)Math.Round(((double)(sent + failed) / total) * 100) : 0,
            DateTimeOffset.TryParse(Get("createdAt"), out var c) ? c : default,
            DateTimeOffset.TryParse(Get("updatedAt"), out var u) ? u : default,
            errors);
    }

    public async Task<ResolvedMessage?> GetMessageAsync(string jobId, CancellationToken ct = default)
    {
        var values = await Db.HashGetAsync(Key(jobId), new RedisValue[] { "message", "messageType" }).ConfigureAwait(false);
        if (values[0].IsNullOrEmpty)
        {
            return null;
        }
        var type = string.Equals((string?)values[1], "card", StringComparison.OrdinalIgnoreCase)
            ? MessageType.Card : MessageType.Text;
        return new ResolvedMessage(type, (string?)values[0] ?? string.Empty);
    }

    public Task<bool> PingAsync(CancellationToken ct = default) => _redis.PingAsync(ct);
}
