// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TeamsMsgs.Shared.Configuration;

namespace TeamsMsgs.Api.Auth;

/// <summary>Reusable API-key validator for route-group filters.</summary>
public static class ApiKeyEndpointFilter
{
    private const string HeaderName = "x-api-key";

    public static Task ValidateAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var options = context.RequestServices.GetRequiredService<IOptions<ApiOptions>>().Value;
        if (string.IsNullOrEmpty(options.ApiKey))
        {
            return Task.CompletedTask;
        }

        if (!context.Request.Headers.TryGetValue(HeaderName, out var header))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        var headerBytes = Encoding.UTF8.GetBytes(header.ToString());
        var expectedBytes = Encoding.UTF8.GetBytes(options.ApiKey);
        if (headerBytes.Length != expectedBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(headerBytes, expectedBytes))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        }
        return Task.CompletedTask;
    }
}
