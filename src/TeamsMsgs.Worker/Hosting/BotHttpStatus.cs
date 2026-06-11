// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

using System.Net;

namespace TeamsMsgs.Worker.Hosting;

/// <summary>Extracts HTTP status and Retry-After from Bot Framework exceptions.</summary>
internal static class BotHttpStatus
{
    public static int? ExtractStatus(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        // ErrorResponseException é o tipo mais comum do Microsoft.Bot.Connector.
        // Reflection é usada para evitar dependência de assembly específico.
        var statusProp = ex.GetType().GetProperty("StatusCode");
        if (statusProp is not null)
        {
            var value = statusProp.GetValue(ex);
            if (value is HttpStatusCode http)
            {
                return (int)http;
            }
            if (value is int code)
            {
                return code;
            }
        }

        // HttpRequestException expõe StatusCode em .NET 6+
        if (ex is HttpRequestException hre && hre.StatusCode.HasValue)
        {
            return (int)hre.StatusCode.Value;
        }

        return null;
    }

    public static TimeSpan? ExtractRetryAfter(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        // Tenta achar Response.Headers["Retry-After"]
        var responseProp = ex.GetType().GetProperty("Response");
        if (responseProp?.GetValue(ex) is { } response)
        {
            var headersProp = response.GetType().GetProperty("Headers");
            if (headersProp?.GetValue(response) is System.Collections.IEnumerable headers)
            {
                foreach (var header in headers)
                {
                    var nameProp = header.GetType().GetProperty("Key");
                    if (nameProp?.GetValue(header) is string name &&
                        string.Equals(name, "Retry-After", StringComparison.OrdinalIgnoreCase))
                    {
                        var valueProp = header.GetType().GetProperty("Value");
                        var first = (valueProp?.GetValue(header) as System.Collections.IEnumerable)?
                            .Cast<string>().FirstOrDefault();
                        if (int.TryParse(first, out var seconds))
                        {
                            return TimeSpan.FromSeconds(seconds);
                        }
                    }
                }
            }
        }
        return null;
    }
}
