// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

using System.Reflection;
using FluentAssertions;
using TeamsMsgs.Shared.Jobs;
using TeamsMsgs.Shared.Storage;

namespace TeamsMsgs.Tests;

public sealed class RowKeyTests
{
    // BuildRowKey é internal — acessamos via reflection.
    private static string BuildSentMarkRowKey(string refRowKey, int repeatIndex)
    {
        var method = typeof(TableSentMarkStore).GetMethod(
            "BuildRowKey",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, new object[] { refRowKey, repeatIndex })!;
    }

    private static string ToSafeRowKey(string conversationId)
    {
        var method = typeof(ConversationRefStore).GetMethod(
            "ToSafeRowKey",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, new object[] { conversationId })!;
    }

    [Fact]
    public void SentMarkRowKey_IsDeterministic()
    {
        var a = BuildSentMarkRowKey("user-123", 0);
        var b = BuildSentMarkRowKey("user-123", 0);
        a.Should().Be(b);
    }

    [Fact]
    public void SentMarkRowKey_DiffersByRepeatIndex()
    {
        var a = BuildSentMarkRowKey("user-123", 0);
        var b = BuildSentMarkRowKey("user-123", 1);
        a.Should().NotBe(b);
        a.Should().EndWith("_r0");
        b.Should().EndWith("_r1");
    }

    [Fact]
    public void SentMarkRowKey_LengthMatchesMd5HexPlusSuffix()
    {
        var key = BuildSentMarkRowKey("any-id", 7);
        // md5 hex (32) + "_r" (2) + "7" (1) = 35
        key.Length.Should().Be(35);
    }

    [Fact]
    public void SafeRowKey_IsBase64UrlEncoded()
    {
        var key = ToSafeRowKey("a:b/c+d=e");
        key.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
    }

    [Fact]
    public void SafeRowKey_IsDeterministic()
    {
        var a = ToSafeRowKey("conversation-1");
        var b = ToSafeRowKey("conversation-1");
        a.Should().Be(b);
    }

    [Fact]
    public void SafeRowKey_DiffersByInput()
    {
        var a = ToSafeRowKey("conversation-1");
        var b = ToSafeRowKey("conversation-2");
        a.Should().NotBe(b);
    }
}
