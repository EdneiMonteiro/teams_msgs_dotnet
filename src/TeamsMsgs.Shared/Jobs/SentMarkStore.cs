// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// Substituto da dedup nativa do Service Bus (`messageId`).

using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TeamsMsgs.Shared.Azure;
using TeamsMsgs.Shared.Configuration;

namespace TeamsMsgs.Shared.Jobs;

public interface ISentMarkStore
{
    /// <summary>
    /// Tries to claim the send slot for (jobId, refRowKey, repeatIndex).
    /// Returns true if the caller should proceed; false if already claimed.
    /// </summary>
    Task<bool> TryClaimAsync(string jobId, string refRowKey, int repeatIndex, CancellationToken ct = default);

    Task EnsureCreatedAsync(CancellationToken ct = default);
}

public sealed class TableSentMarkStore : ISentMarkStore
{
    private readonly TableClient _table;
    private readonly ILogger<TableSentMarkStore> _logger;
    private bool _ensured;

    public TableSentMarkStore(
        AzureClientFactory factory,
        IOptions<StorageOptions> options,
        ILogger<TableSentMarkStore> logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);
        _table = factory.CreateTableClient(options.Value.SentMarksTableName);
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

    internal static string BuildRowKey(string refRowKey, int repeatIndex)
    {
        var bytes = Encoding.UTF8.GetBytes(refRowKey);
        var hash = MD5.HashData(bytes);
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"{hex}_r{repeatIndex}";
    }

    public async Task<bool> TryClaimAsync(string jobId, string refRowKey, int repeatIndex, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct).ConfigureAwait(false);
        var entity = new TableEntity(jobId, BuildRowKey(refRowKey, repeatIndex))
        {
            ["claimedAt"] = DateTimeOffset.UtcNow,
        };
        try
        {
            await _table.AddEntityAsync(entity, ct).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            return false;
        }
    }
}
