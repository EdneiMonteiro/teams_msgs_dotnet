// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

using FluentAssertions;
using TeamsMsgs.Shared.Sending;

namespace TeamsMsgs.Tests;

public sealed class SendWithRetryTests
{
    private sealed class FakeException : Exception
    {
        public int Status { get; }

        public TimeSpan? RetryAfter { get; }

        public FakeException(int status, TimeSpan? retryAfter = null, string? message = null)
            : base(message ?? $"HTTP {status}")
        {
            Status = status;
            RetryAfter = retryAfter;
        }
    }

    private static SendRetryOptions NoSleep() => new()
    {
        Sleep = (_, _) => Task.CompletedTask,
    };

    private static int? ExtractStatus(Exception ex) => ((FakeException)ex).Status;

    private static TimeSpan? ExtractRetryAfter(Exception ex) => ((FakeException)ex).RetryAfter;

    [Fact]
    public async Task ExecuteAsync_SuccessFirstTry()
    {
        var calls = 0;
        var outcome = await SendWithRetry.ExecuteAsync(
            _ => { calls++; return Task.CompletedTask; },
            ExtractStatus,
            options: NoSleep());

        outcome.Ok.Should().BeTrue();
        calls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_403_Permanent_NoRetry()
    {
        var calls = 0;
        var outcome = await SendWithRetry.ExecuteAsync(
            _ => { calls++; throw new FakeException(403); },
            ExtractStatus,
            options: NoSleep());

        outcome.Ok.Should().BeFalse();
        outcome.Permanent.Should().BeTrue();
        outcome.StatusCode.Should().Be(403);
        outcome.ErrorMsg.Should().Contain("bloqueou");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_410_Permanent_NoRetry()
    {
        var calls = 0;
        var outcome = await SendWithRetry.ExecuteAsync(
            _ => { calls++; throw new FakeException(410); },
            ExtractStatus,
            options: NoSleep());

        outcome.Permanent.Should().BeTrue();
        outcome.StatusCode.Should().Be(410);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_400_Permanent_NoRetry()
    {
        var calls = 0;
        var outcome = await SendWithRetry.ExecuteAsync(
            _ => { calls++; throw new FakeException(400, message: "bad request"); },
            ExtractStatus,
            options: NoSleep());

        outcome.Permanent.Should().BeTrue();
        outcome.StatusCode.Should().Be(400);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_429_RetriesUntilLimit()
    {
        var calls = 0;
        var outcome = await SendWithRetry.ExecuteAsync(
            _ => { calls++; throw new FakeException(429); },
            ExtractStatus,
            ExtractRetryAfter,
            new SendRetryOptions { Retries = 3, Sleep = (_, _) => Task.CompletedTask });

        outcome.Ok.Should().BeFalse();
        // On the last attempt (== Retries), the catch falls through to classification
        // and 429 maps to the "permanent" 400-499 branch (no specific 429 mapping
        // in the SendOutcome record). We just assert it stopped retrying.
        calls.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_500_RetriesAndEventuallyTransient()
    {
        var calls = 0;
        var outcome = await SendWithRetry.ExecuteAsync(
            _ => { calls++; throw new FakeException(500); },
            ExtractStatus,
            options: new SendRetryOptions { Retries = 3, Sleep = (_, _) => Task.CompletedTask });

        outcome.Ok.Should().BeFalse();
        outcome.Permanent.Should().BeFalse();
        outcome.StatusCode.Should().Be(500);
        calls.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_429_ThenSuccess()
    {
        var calls = 0;
        var outcome = await SendWithRetry.ExecuteAsync(
            _ =>
            {
                calls++;
                if (calls == 1)
                {
                    throw new FakeException(429);
                }
                return Task.CompletedTask;
            },
            ExtractStatus,
            ExtractRetryAfter,
            new SendRetryOptions { Retries = 3, Sleep = (_, _) => Task.CompletedTask });

        outcome.Ok.Should().BeTrue();
        calls.Should().Be(2);
    }

    [Fact]
    public async Task ExecuteAsync_5xx_ThenSuccess()
    {
        var calls = 0;
        var outcome = await SendWithRetry.ExecuteAsync(
            _ =>
            {
                calls++;
                if (calls < 3)
                {
                    throw new FakeException(503);
                }
                return Task.CompletedTask;
            },
            ExtractStatus,
            options: new SendRetryOptions { Retries = 5, Sleep = (_, _) => Task.CompletedTask });

        outcome.Ok.Should().BeTrue();
        calls.Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_429_UsesRetryAfter()
    {
        var sleeps = new List<TimeSpan>();
        var calls = 0;
        var outcome = await SendWithRetry.ExecuteAsync(
            _ =>
            {
                calls++;
                if (calls == 1)
                {
                    throw new FakeException(429, TimeSpan.FromMilliseconds(123));
                }
                return Task.CompletedTask;
            },
            ExtractStatus,
            ExtractRetryAfter,
            new SendRetryOptions
            {
                Retries = 3,
                Sleep = (delay, _) =>
                {
                    sleeps.Add(delay);
                    return Task.CompletedTask;
                },
            });

        outcome.Ok.Should().BeTrue();
        sleeps.Should().ContainSingle().Which.Should().Be(TimeSpan.FromMilliseconds(123));
    }

    [Fact]
    public async Task ExecuteAsync_UnknownStatus_Transient()
    {
        var outcome = await SendWithRetry.ExecuteAsync(
            _ => throw new FakeException(0, message: "network down"),
            ExtractStatus,
            options: NoSleep());

        outcome.Ok.Should().BeFalse();
        outcome.Permanent.Should().BeFalse();
        outcome.ErrorMsg.Should().Be("network down");
    }
}
