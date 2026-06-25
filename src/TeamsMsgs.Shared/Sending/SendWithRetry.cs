// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

namespace TeamsMsgs.Shared.Sending;

public sealed record SendOutcome(bool Ok, bool Permanent, int StatusCode, string? ErrorMsg)
{
    public static SendOutcome Success() => new(true, false, 0, null);
}

public sealed class SendRetryOptions
{
    public int Retries { get; init; } = 3;

    public Func<TimeSpan, CancellationToken, Task> Sleep { get; init; } =
        static (delay, ct) => Task.Delay(delay, ct);
}

public delegate int? StatusExtractor(Exception ex);

public static class SendWithRetry
{
    /// <summary>
    /// Executes <paramref name="send"/> with retry/backoff classification:
    /// - 429 → Retry-After (if available) or exp backoff, up to Retries;
    /// - 5xx → exp backoff, up to Retries;
    /// - 403/410 → permanent, no retry, specific message;
    /// - other 4xx → permanent, no retry;
    /// - unknown → transient, surface for upstream redelivery.
    /// </summary>
    public static async Task<SendOutcome> ExecuteAsync(
        Func<CancellationToken, Task> send,
        StatusExtractor extractStatus,
        Func<Exception, TimeSpan?>? extractRetryAfter = null,
        SendRetryOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(extractStatus);
        options ??= new SendRetryOptions();

        for (var attempt = 1; attempt <= options.Retries; attempt++)
        {
            try
            {
                await send(ct).ConfigureAwait(false);
                return SendOutcome.Success();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancelamento cooperativo real (shutdown do host) — propaga.
                throw;
            }
            catch (Exception ex)
            {
                // Inclui TaskCanceledException de timeout do HttpClient (ct NÃO cancelado):
                // deve ser tratado como falha transitória de envio, não como shutdown.
                var status = extractStatus(ex) ?? 0;

                if (status == 429 && attempt < options.Retries)
                {
                    var retryAfter = extractRetryAfter?.Invoke(ex)
                        ?? TimeSpan.FromMilliseconds(1000 * Math.Pow(2, attempt));
                    await options.Sleep(retryAfter, ct).ConfigureAwait(false);
                    continue;
                }

                if (status >= 500 && attempt < options.Retries)
                {
                    var backoff = TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt));
                    await options.Sleep(backoff, ct).ConfigureAwait(false);
                    continue;
                }

                return status switch
                {
                    403 => new SendOutcome(false, true, 403, "Usuário bloqueou/desinstalou o bot"),
                    410 => new SendOutcome(false, true, 410, "Conversa não existe mais (410)"),
                    >= 400 and < 500 => new SendOutcome(false, true, status, ex.Message),
                    _ => new SendOutcome(false, false, status, ex.Message),
                };
            }
        }

        return new SendOutcome(false, false, 0, "Max retries exceeded");
    }
}
