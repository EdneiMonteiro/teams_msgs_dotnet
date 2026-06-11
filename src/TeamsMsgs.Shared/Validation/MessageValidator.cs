// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.
//
// Port of src/validate-message.ts. Accepts either a non-empty string OR
// an AdaptiveCard envelope with the shape:
//   { "type": "AdaptiveCard", "content": { "type": "AdaptiveCard", ... } }

using System.Text.Json;

namespace TeamsMsgs.Shared.Validation;

public static class MessageValidator
{
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web);

    public static MessageValidationResult Validate(JsonElement? input)
    {
        if (input is null)
        {
            return new MessageValidationResult.Failure(
                "Campo 'message' deve ser string OU { type: 'AdaptiveCard', content: <card> }");
        }

        var element = input.Value;

        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return new MessageValidationResult.Failure(
                    "Campo 'message' (string) não pode ser vazio");
            }
            return new MessageValidationResult.Success(new ValidatedMessage(MessageType.Text, text));
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (!element.TryGetProperty("type", out var typeProp) ||
                typeProp.ValueKind != JsonValueKind.String ||
                !string.Equals(typeProp.GetString(), "AdaptiveCard", StringComparison.Ordinal))
            {
                return new MessageValidationResult.Failure(
                    "Campo 'message' deve ser string OU { type: 'AdaptiveCard', content: <card> }");
            }

            if (!element.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Object)
            {
                return new MessageValidationResult.Failure(
                    "AdaptiveCard precisa de 'content' (objeto)");
            }

            if (!content.TryGetProperty("type", out var innerType) ||
                innerType.ValueKind != JsonValueKind.String ||
                !string.Equals(innerType.GetString(), "AdaptiveCard", StringComparison.Ordinal))
            {
                return new MessageValidationResult.Failure(
                    "AdaptiveCard 'content' precisa ter type='AdaptiveCard'");
            }

            var serialized = JsonSerializer.Serialize(element, WriteOptions);
            return new MessageValidationResult.Success(new ValidatedMessage(MessageType.Card, serialized));
        }

        return new MessageValidationResult.Failure(
            "Campo 'message' deve ser string OU { type: 'AdaptiveCard', content: <card> }");
    }
}
