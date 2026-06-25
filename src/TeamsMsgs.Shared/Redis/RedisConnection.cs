// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// Singleton ConnectionMultiplexer (StackExchange.Redis). O Redis volta a ser
// usado para: job counters (HINCRBY), índice de refs (SCARD), cache do payload
// da mensagem e token bucket global (rate limit).

using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TeamsMsgs.Shared.Configuration;

namespace TeamsMsgs.Shared.Redis;

public interface IRedisConnection
{
    IDatabase Database { get; }

    Task<bool> PingAsync(CancellationToken ct = default);
}

public sealed class RedisConnection : IRedisConnection, IDisposable
{
    private readonly Lazy<ConnectionMultiplexer> _mux;

    public RedisConnection(IOptions<RedisOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var connStr = options.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connStr))
        {
            throw new InvalidOperationException("Redis não configurado: defina Redis:ConnectionString.");
        }

        var config = ConfigurationOptions.Parse(connStr);
        config.AbortOnConnectFail = false;
        config.ConnectRetry = 3;
        _mux = new Lazy<ConnectionMultiplexer>(() => ConnectionMultiplexer.Connect(config));
    }

    public IDatabase Database => _mux.Value.GetDatabase();

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            await Database.PingAsync().ConfigureAwait(false);
            return true;
        }
        catch (RedisException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_mux.IsValueCreated)
        {
            _mux.Value.Dispose();
        }
    }
}
