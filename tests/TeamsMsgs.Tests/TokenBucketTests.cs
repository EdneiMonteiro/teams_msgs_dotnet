// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

using FluentAssertions;
using TeamsMsgs.Shared.RateLimiting;

namespace TeamsMsgs.Tests;

public sealed class TokenBucketTests
{
    [Fact]
    public void Step_AllowsWhenTokensAvailable()
    {
        var state = new BucketState(Tokens: 5, TimestampMs: 1000);
        var (next, allowed) = RedisTokenBucket.Step(state, capacity: 50, ratePerSec: 50, nowMs: 1000);
        allowed.Should().BeTrue();
        next.Tokens.Should().BeApproximately(4, 0.001);
    }

    [Fact]
    public void Step_DeniesWhenEmptyAndNoElapsed()
    {
        var state = new BucketState(Tokens: 0, TimestampMs: 1000);
        var (next, allowed) = RedisTokenBucket.Step(state, capacity: 50, ratePerSec: 50, nowMs: 1000);
        allowed.Should().BeFalse();
        next.Tokens.Should().BeApproximately(0, 0.001);
    }

    [Fact]
    public void Step_RefillsOverTime()
    {
        var state = new BucketState(Tokens: 0, TimestampMs: 1000);
        // 1s elapsed at 50 tokens/s → ~50 tokens, consumes 1.
        var (next, allowed) = RedisTokenBucket.Step(state, capacity: 50, ratePerSec: 50, nowMs: 2000);
        allowed.Should().BeTrue();
        next.Tokens.Should().BeApproximately(49, 0.001);
    }

    [Fact]
    public void Step_CapsAtCapacity()
    {
        var state = new BucketState(Tokens: 10, TimestampMs: 1000);
        // 100s elapsed would add 5000, but capacity caps at 50, then consume 1.
        var (next, allowed) = RedisTokenBucket.Step(state, capacity: 50, ratePerSec: 50, nowMs: 101000);
        allowed.Should().BeTrue();
        next.Tokens.Should().BeApproximately(49, 0.001);
    }
}
