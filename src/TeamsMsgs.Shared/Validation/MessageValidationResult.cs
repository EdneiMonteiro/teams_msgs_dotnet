// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

namespace TeamsMsgs.Shared.Validation;

/// <summary>Type of message payload accepted by the API.</summary>
public enum MessageType
{
    Text,
    Card,
}

/// <summary>Result of a successful validation.</summary>
public sealed record ValidatedMessage(MessageType Type, string Serialized);

/// <summary>Outcome of <see cref="MessageValidator"/>.</summary>
public abstract record MessageValidationResult
{
    public sealed record Success(ValidatedMessage Message) : MessageValidationResult;

    public sealed record Failure(string Error) : MessageValidationResult;
}
