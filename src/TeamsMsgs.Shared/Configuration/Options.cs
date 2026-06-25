// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

namespace TeamsMsgs.Shared.Configuration;

/// <summary>Storage Account (Table) connectivity — durable conversation refs.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Connection string for the Storage Account (Table service).</summary>
    public string? ConnectionString { get; set; }

    public string RefsTableName { get; set; } = "conversationrefs";
}

/// <summary>Azure Service Bus connectivity (replaces Storage Queue).</summary>
public sealed class ServiceBusOptions
{
    public const string SectionName = "ServiceBus";

    public string? ConnectionString { get; set; }

    public string QueueName { get; set; } = "send-messages";

    /// <summary>Deliveries before the broker dead-letters the message (native DLQ).</summary>
    public int MaxDeliveryCount { get; set; } = 5;
}

/// <summary>Redis connectivity — job counters, refs index, message cache, rate-limit bucket.</summary>
public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    public string? ConnectionString { get; set; }
}

/// <summary>Global token-bucket rate limit (Redis-backed) for Bot Framework egress.</summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    public bool Enabled { get; set; } = true;

    public int Capacity { get; set; } = 50;

    public int RatePerSec { get; set; } = 50;

    public string Key { get; set; } = "ratelimit:bot";
}

public sealed class BotOptions
{
    public const string SectionName = "Bot";

    public string AppId { get; set; } = string.Empty;

    public string AppPassword { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;
}

public sealed class ApiOptions
{
    public const string SectionName = "Api";

    public string ApiKey { get; set; } = string.Empty;

    public int SendFlushConcurrency { get; set; } = 5;
}

public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    public int MaxConcurrent { get; set; } = 10;

    public int PollBatchSize { get; set; } = 32;

    public TimeSpan VisibilityTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan EmptyQueueBackoff { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan MessageCacheTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Timeout do HttpClient usado pelo conector do Bot Framework nos envios.
    /// Curto de propósito: o default de 100s prende slots de concorrência quando
    /// um destinatário "pendura" a requisição, colapsando a vazão em massa.
    /// </summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(20);
}
