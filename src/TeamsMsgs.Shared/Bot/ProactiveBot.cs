// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

using Microsoft.Bot.Builder;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;

namespace TeamsMsgs.Shared.Bot;

public interface IRefStore
{
    Task SaveAsync(ConversationReference reference, CancellationToken ct = default);

    Task RemoveAsync(string conversationId, CancellationToken ct = default);
}

public sealed class ProactiveBot : ActivityHandler
{
    private readonly IRefStore _store;
    private readonly ILogger<ProactiveBot> _logger;

    public ProactiveBot(IRefStore store, ILogger<ProactiveBot> logger)
    {
        _store = store;
        _logger = logger;
    }

    protected override async Task OnConversationUpdateActivityAsync(
        ITurnContext<IConversationUpdateActivity> turnContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turnContext);
        var activity = turnContext.Activity;
        var botId = activity.Recipient?.Id;

        if (activity.MembersAdded is not null)
        {
            foreach (var member in activity.MembersAdded)
            {
                if (!string.Equals(member.Id, botId, StringComparison.Ordinal))
                {
                    var reference = activity.GetConversationReference();
                    await _store.SaveAsync(reference, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (activity.MembersRemoved is not null)
        {
            foreach (var member in activity.MembersRemoved)
            {
                if (!string.Equals(member.Id, botId, StringComparison.Ordinal))
                {
                    var reference = activity.GetConversationReference();
                    var conversationId = reference.Conversation?.Id;
                    if (!string.IsNullOrEmpty(conversationId))
                    {
                        await _store.RemoveAsync(conversationId!, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        await base.OnConversationUpdateActivityAsync(turnContext, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task OnMessageActivityAsync(
        ITurnContext<IMessageActivity> turnContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turnContext);
        var reference = turnContext.Activity.GetConversationReference();
        await _store.SaveAsync(reference, cancellationToken).ConfigureAwait(false);
        await turnContext
            .SendActivityAsync(
                MessageFactory.Text("✅ Sua referência foi registrada. Você receberá notificações neste chat."),
                cancellationToken)
            .ConfigureAwait(false);
    }

    protected override async Task OnInstallationUpdateActivityAsync(
        ITurnContext<IInstallationUpdateActivity> turnContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(turnContext);
        if (string.Equals(turnContext.Activity.Action, "add", StringComparison.OrdinalIgnoreCase))
        {
            var reference = turnContext.Activity.GetConversationReference();
            await _store.SaveAsync(reference, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("App instalado, referência salva ({ConversationId}).",
                reference.Conversation?.Id);
        }
    }
}
