// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Integration.AspNet.Core;

namespace TeamsMsgs.Api.Endpoints;

public static class BotEndpoint
{
    public static IEndpointRouteBuilder MapBotEndpoint(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/messages", async (
            HttpRequest request,
            HttpResponse response,
            IBotFrameworkHttpAdapter adapter,
            IBot bot,
            CancellationToken ct) =>
        {
            await adapter.ProcessAsync(request, response, bot, ct).ConfigureAwait(false);
        });

        return app;
    }
}
