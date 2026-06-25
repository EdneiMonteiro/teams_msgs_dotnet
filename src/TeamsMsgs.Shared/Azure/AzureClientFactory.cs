// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

using Azure.Data.Tables;
using Microsoft.Extensions.Options;
using TeamsMsgs.Shared.Configuration;

namespace TeamsMsgs.Shared.Azure;

/// <summary>
/// Creates Azure Table clients from a Storage connection string.
/// (Connection-string auth — Workload Identity foi removido nesta versão.)
/// </summary>
public sealed class AzureClientFactory
{
    private readonly StorageOptions _options;

    public AzureClientFactory(IOptions<StorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public TableClient CreateTableClient(string tableName)
    {
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            throw new InvalidOperationException(
                "Storage não configurado: defina Storage:ConnectionString.");
        }

        return new TableClient(_options.ConnectionString, tableName);
    }
}
