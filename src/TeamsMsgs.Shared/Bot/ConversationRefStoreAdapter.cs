// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

using System.Text.Json;
using Microsoft.Bot.Schema;
using TeamsMsgs.Shared.Storage;

namespace TeamsMsgs.Shared.Bot;

/// <summary>Bridges Bot Framework ConversationReference persistence into IConversationRefStore.</summary>
public sealed class ConversationRefStoreAdapter : IRefStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IConversationRefStore _store;

    public ConversationRefStoreAdapter(IConversationRefStore store)
    {
        _store = store;
    }

    public async Task SaveAsync(ConversationReference reference, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var conversationId = reference.Conversation?.Id;
        if (string.IsNullOrEmpty(conversationId))
        {
            return;
        }
        var json = JsonSerializer.Serialize(reference, JsonOptions);
        await _store.SaveAsync(conversationId!, json, ct).ConfigureAwait(false);
    }

    public Task RemoveAsync(string conversationId, CancellationToken ct = default)
        => _store.RemoveAsync(conversationId, ct);
}
