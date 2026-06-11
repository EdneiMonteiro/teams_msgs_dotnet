// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

namespace TeamsMsgs.Shared.Configuration;

/// <summary>Options for Storage Account (tables + queues) connectivity.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Optional connection string (dev). In prod prefer service URIs + Managed Identity.</summary>
    public string? ConnectionString { get; set; }

    public string? TableServiceUri { get; set; }

    public string? QueueServiceUri { get; set; }

    public string RefsTableName { get; set; } = "conversationrefs";

    public string JobsTableName { get; set; } = "jobs";

    public string SentMarksTableName { get; set; } = "sentmarks";

    public string QueueName { get; set; } = "send-messages";

    public string PoisonQueueName { get; set; } = "send-messages-poison";

    public int MaxDequeueCount { get; set; } = 5;
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
}
