// <copyright file="LlmResponseParser.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Api
{
    using System;
    using System.Linq;
    using System.Text.Json;
    using Jellyfin.Plugin.MetaShark.Configuration;

    public static class LlmResponseParser
    {
        public static LlmApiResult Parse(string responseJson, string structuredOutputMode)
        {
            if (string.IsNullOrWhiteSpace(responseJson))
            {
                return LlmApiResult.Failed("LLM response is empty.");
            }

            try
            {
                using var document = JsonDocument.Parse(responseJson);
                var root = document.RootElement;
                if (root.TryGetProperty("error", out var errorElement))
                {
                    return LlmApiResult.Failed(ParseErrorEnvelope(errorElement));
                }

                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                {
                    return LlmApiResult.Failed("LLM response choices is empty.");
                }

                var firstChoice = choices[0];
                var finishReason = GetString(firstChoice, "finish_reason");
                if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(finishReason, "content_filter", StringComparison.OrdinalIgnoreCase))
                {
                    return LlmApiResult.Failed($"LLM response finish_reason={finishReason}.");
                }

                if (!firstChoice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                {
                    return LlmApiResult.Failed("LLM response message is missing.");
                }

                var refusal = GetString(message, "refusal");
                if (!string.IsNullOrWhiteSpace(refusal))
                {
                    return LlmApiResult.Failed("LLM response contains refusal.");
                }

                var content = GetString(message, "content");
                if (string.IsNullOrWhiteSpace(content))
                {
                    return LlmApiResult.Failed("LLM response message content is empty.");
                }

                var contentJson = string.Equals(structuredOutputMode, PluginConfiguration.LlmStructuredOutputModeTextJson, StringComparison.Ordinal)
                    ? ExtractJsonObject(content)
                    : content.Trim();
                if (contentJson == null)
                {
                    return LlmApiResult.Failed("LLM response content does not contain valid JSON object.");
                }

                if (!IsValidSchemaObject(contentJson))
                {
                    return LlmApiResult.Failed("LLM response content schema invalid.");
                }

                return LlmApiResult.Succeeded(contentJson);
            }
            catch (JsonException ex)
            {
                return LlmApiResult.Failed($"LLM response invalid JSON: {ex.Message}");
            }
        }

        public static string ParseErrorDiagnostic(string responseJson)
        {
            if (string.IsNullOrWhiteSpace(responseJson))
            {
                return "LLM error response is empty.";
            }

            try
            {
                using var document = JsonDocument.Parse(responseJson);
                if (document.RootElement.TryGetProperty("error", out var errorElement))
                {
                    return ParseErrorEnvelope(errorElement);
                }
            }
            catch (JsonException ex)
            {
                return $"LLM error response invalid JSON: {ex.Message}";
            }

            return "LLM request failed.";
        }

        private static string ParseErrorEnvelope(JsonElement errorElement)
        {
            if (errorElement.ValueKind != JsonValueKind.Object)
            {
                return "LLM error envelope is invalid.";
            }

            var message = GetString(errorElement, "message");
            var type = GetString(errorElement, "type");
            var param = GetString(errorElement, "param");
            var code = GetString(errorElement, "code");
            var diagnostic = string.Join(", ", new[]
            {
                string.IsNullOrWhiteSpace(message) ? null : $"message={message}",
                string.IsNullOrWhiteSpace(type) ? null : $"type={type}",
                string.IsNullOrWhiteSpace(param) ? null : $"param={param}",
                string.IsNullOrWhiteSpace(code) ? null : $"code={code}",
            }.Where(part => part != null));
            return string.IsNullOrWhiteSpace(diagnostic) ? "LLM error envelope is empty." : diagnostic;
        }

        private static string? ExtractJsonObject(string content)
        {
            var trimmed = content.Trim();
            if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
            {
                return trimmed;
            }

            var start = trimmed.IndexOf('{', StringComparison.Ordinal);
            var end = trimmed.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return null;
            }

            return trimmed[start..(end + 1)].Trim();
        }

        private static bool IsValidSchemaObject(string contentJson)
        {
            try
            {
                using var document = JsonDocument.Parse(contentJson);
                return document.RootElement.ValueKind == JsonValueKind.Object;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string? GetString(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property))
            {
                return null;
            }

            return property.ValueKind switch
            {
                JsonValueKind.String => property.GetString(),
                JsonValueKind.Null => null,
                _ => property.ToString(),
            };
        }
    }
}
