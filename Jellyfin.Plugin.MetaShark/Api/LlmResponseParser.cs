// <copyright file="LlmResponseParser.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Api
{
    using System;
    using System.Linq;
    using System.Text.Json;

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

                if (!root.TryGetProperty("choices", out var choices))
                {
                    return ParseContentJson(responseJson, "LLM response content");
                }

                if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
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

                return ParseContentJson(content, "LLM response content");
            }
            catch (JsonException ex)
            {
                var contentResult = ParseContentJson(responseJson, "LLM response content");
                return contentResult.Success ? contentResult : LlmApiResult.Failed($"LLM response invalid JSON: {ex.Message}");
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

        private static LlmApiResult ParseContentJson(string content, string diagnosticPrefix)
        {
            var contentJson = TrimSingleJsonFence(content);
            if (contentJson == null)
            {
                return LlmApiResult.Failed($"{diagnosticPrefix} does not contain a single JSON object.");
            }

            try
            {
                using var document = JsonDocument.Parse(contentJson);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return LlmApiResult.Failed($"{diagnosticPrefix} schema invalid.");
                }
            }
            catch (JsonException ex)
            {
                return LlmApiResult.Failed($"{diagnosticPrefix} invalid JSON: {ex.Message}");
            }

            return LlmApiResult.Succeeded(contentJson.Trim());
        }

        private static string? TrimSingleJsonFence(string content)
        {
            var trimmed = content.Trim();
            if (trimmed.Length == 0)
            {
                return null;
            }

            if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                return trimmed;
            }

            var firstLineEnd = trimmed.IndexOf('\n', StringComparison.Ordinal);
            if (firstLineEnd < 0)
            {
                return null;
            }

            var fenceInfo = trimmed[3..firstLineEnd].Trim();
            if (fenceInfo.Length != 0 && !string.Equals(fenceInfo, "json", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var closingFenceStart = trimmed.LastIndexOf("\n```", StringComparison.Ordinal);
            if (closingFenceStart <= firstLineEnd)
            {
                return null;
            }

            var afterClosingFence = trimmed[(closingFenceStart + 4)..].Trim();
            if (afterClosingFence.Length != 0)
            {
                return null;
            }

            var fencedContent = trimmed[(firstLineEnd + 1)..closingFenceStart].Trim();
            if (fencedContent.StartsWith("```", StringComparison.Ordinal) || fencedContent.Contains("\n```", StringComparison.Ordinal))
            {
                return null;
            }

            return fencedContent.Length == 0 ? null : fencedContent;
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
