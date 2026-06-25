// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

using System.Net;
using FluentAssertions;
using TeamsMsgs.Worker.Hosting;

namespace TeamsMsgs.Tests;

public class BotHttpStatusTests
{
    // Mimetiza Microsoft.Bot.Connector.ErrorResponseException / Microsoft.Rest.HttpOperationException:
    // o status HTTP fica em Response.StatusCode (HttpResponseMessageWrapper), não no topo da exceção.
    private sealed class FakeBotResponse
    {
        public HttpStatusCode StatusCode { get; init; }
    }

    private sealed class FakeBotException : Exception
    {
        public FakeBotException(string message) : base(message) { }

        public FakeBotResponse? Response { get; init; }
    }

    private sealed class TopLevelStatusException : Exception
    {
        public HttpStatusCode StatusCode { get; init; }
    }

    [Fact]
    public void ExtractStatus_reads_BadRequest_from_Response_StatusCode()
    {
        var ex = new FakeBotException("Operation returned an invalid status code 'BadRequest'")
        {
            Response = new FakeBotResponse { StatusCode = HttpStatusCode.BadRequest },
        };

        BotHttpStatus.ExtractStatus(ex).Should().Be(400);
    }

    [Fact]
    public void ExtractStatus_reads_Forbidden_from_Response_StatusCode()
    {
        var ex = new FakeBotException("forbidden")
        {
            Response = new FakeBotResponse { StatusCode = HttpStatusCode.Forbidden },
        };

        BotHttpStatus.ExtractStatus(ex).Should().Be(403);
    }

    [Fact]
    public void ExtractStatus_prefers_top_level_StatusCode_when_present()
    {
        var ex = new TopLevelStatusException { StatusCode = HttpStatusCode.TooManyRequests };

        BotHttpStatus.ExtractStatus(ex).Should().Be(429);
    }

    [Fact]
    public void ExtractStatus_reads_HttpRequestException_status()
    {
        var ex = new HttpRequestException("boom", null, HttpStatusCode.ServiceUnavailable);

        BotHttpStatus.ExtractStatus(ex).Should().Be(503);
    }

    [Fact]
    public void ExtractStatus_returns_null_when_unknown()
    {
        BotHttpStatus.ExtractStatus(new InvalidOperationException("no status here")).Should().BeNull();
    }
}
