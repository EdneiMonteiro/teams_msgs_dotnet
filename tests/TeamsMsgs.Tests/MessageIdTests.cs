// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

using FluentAssertions;
using TeamsMsgs.Shared.Messaging;

namespace TeamsMsgs.Tests;

public sealed class MessageIdTests
{
    [Fact]
    public void Compute_IsDeterministic()
    {
        var a = ServiceBusMessageId.Compute("job1", "row-abc", 0);
        var b = ServiceBusMessageId.Compute("job1", "row-abc", 0);
        a.Should().Be(b);
    }

    [Fact]
    public void Compute_DiffersByRepeatIndex()
    {
        var a = ServiceBusMessageId.Compute("job1", "row-abc", 0);
        var b = ServiceBusMessageId.Compute("job1", "row-abc", 1);
        a.Should().NotBe(b);
        a.Should().EndWith(":0");
        b.Should().EndWith(":1");
    }

    [Fact]
    public void Compute_DiffersByRowKey()
    {
        var a = ServiceBusMessageId.Compute("job1", "row-a", 0);
        var b = ServiceBusMessageId.Compute("job1", "row-b", 0);
        a.Should().NotBe(b);
    }

    [Fact]
    public void Compute_Format_JobIdMd5HexRepeat()
    {
        var id = ServiceBusMessageId.Compute("job1", "row-abc", 3);
        var parts = id.Split(':');
        parts.Should().HaveCount(3);
        parts[0].Should().Be("job1");
        parts[1].Should().HaveLength(32);   // md5 hex
        parts[2].Should().Be("3");
    }
}
