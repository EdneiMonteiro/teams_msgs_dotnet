// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

using System.Text.Json;
using FluentAssertions;
using TeamsMsgs.Shared.Validation;

namespace TeamsMsgs.Tests;

public sealed class MessageValidatorTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Validate_Null_ReturnsFailure()
    {
        var result = MessageValidator.Validate(null);
        result.Should().BeOfType<MessageValidationResult.Failure>();
    }

    [Fact]
    public void Validate_NonEmptyString_ReturnsText()
    {
        var input = Parse("\"hello\"");
        var result = MessageValidator.Validate(input);
        var success = result.Should().BeOfType<MessageValidationResult.Success>().Subject;
        success.Message.Type.Should().Be(MessageType.Text);
        success.Message.Serialized.Should().Be("hello");
    }

    [Fact]
    public void Validate_EmptyString_ReturnsFailure()
    {
        var input = Parse("\"\"");
        var result = MessageValidator.Validate(input);
        result.Should().BeOfType<MessageValidationResult.Failure>();
    }

    [Fact]
    public void Validate_WhitespaceString_ReturnsFailure()
    {
        var input = Parse("\"   \"");
        var result = MessageValidator.Validate(input);
        result.Should().BeOfType<MessageValidationResult.Failure>();
    }

    [Fact]
    public void Validate_ValidAdaptiveCard_ReturnsCard()
    {
        var input = Parse("""
        {
          "type": "AdaptiveCard",
          "content": {
            "type": "AdaptiveCard",
            "version": "1.5",
            "body": [{ "type": "TextBlock", "text": "ok" }]
          }
        }
        """);
        var result = MessageValidator.Validate(input);
        var success = result.Should().BeOfType<MessageValidationResult.Success>().Subject;
        success.Message.Type.Should().Be(MessageType.Card);
        success.Message.Serialized.Should().Contain("\"type\":\"AdaptiveCard\"");
        success.Message.Serialized.Should().Contain("\"content\"");
    }

    [Fact]
    public void Validate_AdaptiveCardWithoutContent_ReturnsFailure()
    {
        var input = Parse("""{ "type": "AdaptiveCard" }""");
        var result = MessageValidator.Validate(input);
        result.Should().BeOfType<MessageValidationResult.Failure>()
            .Which.Error.Should().Contain("content");
    }

    [Fact]
    public void Validate_AdaptiveCardWithBadContentType_ReturnsFailure()
    {
        var input = Parse("""
        {
          "type": "AdaptiveCard",
          "content": { "type": "NotACard" }
        }
        """);
        var result = MessageValidator.Validate(input);
        result.Should().BeOfType<MessageValidationResult.Failure>()
            .Which.Error.Should().Contain("AdaptiveCard");
    }

    [Fact]
    public void Validate_WrongObjectShape_ReturnsFailure()
    {
        var input = Parse("""{ "foo": "bar" }""");
        var result = MessageValidator.Validate(input);
        result.Should().BeOfType<MessageValidationResult.Failure>();
    }

    [Fact]
    public void Validate_NumberPrimitive_ReturnsFailure()
    {
        var input = Parse("42");
        var result = MessageValidator.Validate(input);
        result.Should().BeOfType<MessageValidationResult.Failure>();
    }

    [Fact]
    public void Validate_ArrayPrimitive_ReturnsFailure()
    {
        var input = Parse("[1,2,3]");
        var result = MessageValidator.Validate(input);
        result.Should().BeOfType<MessageValidationResult.Failure>();
    }
}
