// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

using System.Reflection;
using FluentAssertions;
using TeamsMsgs.Shared.Storage;

namespace TeamsMsgs.Tests;

public sealed class RowKeyTests
{
    private static string ToSafeRowKey(string conversationId)
    {
        var method = typeof(ConversationRefStore).GetMethod(
            "ToSafeRowKey",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, new object[] { conversationId })!;
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
