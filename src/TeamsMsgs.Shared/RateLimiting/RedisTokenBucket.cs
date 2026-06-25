// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// Token bucket global em Redis (Lua atômico), idêntico ao redis-tracker.ts.
// Limita a taxa TOTAL de envios ao Bot Framework, independente do nº de workers
// (substitui o EnvoyFilter local_ratelimit, que era por pod).

using StackExchange.Redis;
using TeamsMsgs.Shared.Redis;

namespace TeamsMsgs.Shared.RateLimiting;

public interface IRateLimiter
{
    /// <summary>Tenta consumir 1 token. Retorna true se concedido.</summary>
    Task<bool> TryAcquireAsync(CancellationToken ct = default);

    /// <summary>Bloqueia (backoff + jitter) até obter 1 token.</summary>
    Task AcquireAsync(CancellationToken ct = default);
}

public sealed record BucketState(double Tokens, double TimestampMs);

public sealed class RedisTokenBucket : IRateLimiter
{
    // KEYS[1]=bucket  ARGV[1]=capacity ARGV[2]=rate ARGV[3]=now_ms
    private const string Lua = @"
local capacity = tonumber(ARGV[1])
local rate     = tonumber(ARGV[2])
local now_ms   = tonumber(ARGV[3])
local data = redis.call('HMGET', KEYS[1], 'tokens', 'ts')
local tokens = tonumber(data[1])
local last   = tonumber(data[2])
if tokens == nil then tokens = capacity end
if last   == nil then last   = now_ms end
local elapsed = math.max(0, now_ms - last) / 1000.0
tokens = math.min(capacity, tokens + elapsed * rate)
local allowed = 0
if tokens >= 1 then
  tokens = tokens - 1
  allowed = 1
end
redis.call('HSET', KEYS[1], 'tokens', tokens, 'ts', now_ms)
redis.call('EXPIRE', KEYS[1], 60)
return allowed
";

    private readonly IRedisConnection _redis;
    private readonly string _key;
    private readonly int _capacity;
    private readonly int _ratePerSec;

    public RedisTokenBucket(IRedisConnection redis, string key, int capacity, int ratePerSec)
    {
        ArgumentNullException.ThrowIfNull(redis);
        _redis = redis;
        _key = key;
        _capacity = capacity;
        _ratePerSec = ratePerSec;
    }

    public async Task<bool> TryAcquireAsync(CancellationToken ct = default)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var result = await _redis.Database.ScriptEvaluateAsync(
            Lua,
            new RedisKey[] { _key },
            new RedisValue[] { _capacity, _ratePerSec, nowMs }).ConfigureAwait(false);
        return (long)result == 1;
    }

    public async Task AcquireAsync(CancellationToken ct = default)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            if (await TryAcquireAsync(ct).ConfigureAwait(false))
            {
                return;
            }
            attempt++;
            var baseMs = Math.Min(20 + (attempt * 5), 250);
            var jitter = Random.Shared.Next(0, 50);
            await Task.Delay(baseMs + jitter, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Função pura do bucket (para testes sem Redis). Espelha o Lua acima.</summary>
    public static (BucketState State, bool Allowed) Step(BucketState state, int capacity, int ratePerSec, double nowMs)
    {
        var elapsed = Math.Max(0, nowMs - state.TimestampMs) / 1000.0;
        var tokens = Math.Min(capacity, state.Tokens + (elapsed * ratePerSec));
        var allowed = false;
        if (tokens >= 1)
        {
            tokens -= 1;
            allowed = true;
        }
        return (new BucketState(tokens, nowMs), allowed);
    }
}
