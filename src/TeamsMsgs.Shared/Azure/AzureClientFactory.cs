// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Queues;
using Microsoft.Extensions.Options;
using TeamsMsgs.Shared.Configuration;

namespace TeamsMsgs.Shared.Azure;

/// <summary>
/// Creates Azure SDK clients honoring <see cref="StorageOptions"/>:
/// uses Managed Identity (DefaultAzureCredential) when only a service URI
/// is set, otherwise falls back to a connection string for local dev.
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
        if (!string.IsNullOrWhiteSpace(_options.TableServiceUri))
        {
            return new TableClient(new Uri(_options.TableServiceUri), tableName, new DefaultAzureCredential());
        }

        if (!string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            return new TableClient(_options.ConnectionString, tableName);
        }

        throw new InvalidOperationException(
            "Storage não configurado: defina Storage:TableServiceUri (MI) ou Storage:ConnectionString.");
    }

    public QueueClient CreateQueueClient(string queueName)
    {
        var clientOptions = new QueueClientOptions
        {
            MessageEncoding = QueueMessageEncoding.Base64,
        };

        if (!string.IsNullOrWhiteSpace(_options.QueueServiceUri))
        {
            var uri = new Uri(new Uri(_options.QueueServiceUri.TrimEnd('/') + "/"), queueName);
            return new QueueClient(uri, new DefaultAzureCredential(), clientOptions);
        }

        if (!string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            return new QueueClient(_options.ConnectionString, queueName, clientOptions);
        }

        throw new InvalidOperationException(
            "Storage não configurado: defina Storage:QueueServiceUri (MI) ou Storage:ConnectionString.");
    }
}
