// <copyright file="LlmSuggestionValidator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Text.Json;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Logging.Abstractions;

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Validator is intentionally injectable for assist service composition tests.")]
    public sealed class LlmSuggestionValidator
    {
        private const int MaxTitleLength = 200;
        private const int MaxOverviewLength = 4000;

        private static readonly Action<ILogger, string, Exception?> LogIgnoredUnknownSuggestionField =
            LoggerMessage.Define<string>(LogLevel.Debug, new EventId(1, nameof(LogIgnoredUnknownSuggestionField)), "[MetaShark] LLM metadata suggestion ignored unknown field. Field={FieldName}");

        private static readonly HashSet<string> AllowedMediaTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Movie",
            "Series",
            "Season",
            "Episode",
        };

        private readonly ILogger<LlmSuggestionValidator> logger;

        public LlmSuggestionValidator()
            : this(NullLogger<LlmSuggestionValidator>.Instance)
        {
        }

        public LlmSuggestionValidator(ILogger<LlmSuggestionValidator> logger)
        {
            this.logger = logger ?? NullLogger<LlmSuggestionValidator>.Instance;
        }

        public LlmSuggestionValidationResult ParseAndValidate(string? contentJson, double confidenceThreshold)
        {
            if (string.IsNullOrWhiteSpace(contentJson))
            {
                return LlmSuggestionValidationResult.Failed("LLM suggestion JSON is empty.");
            }

            try
            {
                using var document = JsonDocument.Parse(contentJson);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return LlmSuggestionValidationResult.Failed("LLM suggestion schema invalid.");
                }

                if (root.TryGetProperty("suggestions", out var suggestions))
                {
                    var topLevelFieldResult = ValidateSuggestionsEnvelopeFields(root);
                    if (!topLevelFieldResult.Success)
                    {
                        return topLevelFieldResult;
                    }

                    return this.ParseAndValidateSuggestions(suggestions, confidenceThreshold);
                }

                return this.ParseAndValidateSuggestion(root, confidenceThreshold);
            }
            catch (JsonException ex)
            {
                return LlmSuggestionValidationResult.Failed($"LLM suggestion schema invalid: {ex.Message}");
            }
        }

        public LlmSuggestionValidationResult Validate(LlmScrapingSuggestion? suggestion, double confidenceThreshold)
        {
            if (suggestion == null)
            {
                return LlmSuggestionValidationResult.Failed("LLM suggestion is empty.");
            }

            suggestion.MediaType = NormalizeText(suggestion.MediaType);
            suggestion.Title = NormalizeText(suggestion.Title);
            suggestion.OriginalTitle = NormalizeText(suggestion.OriginalTitle);
            suggestion.Overview = NormalizeText(suggestion.Overview);

            if (!string.IsNullOrWhiteSpace(suggestion.MediaType) && !AllowedMediaTypes.Contains(suggestion.MediaType))
            {
                return LlmSuggestionValidationResult.Failed("LLM suggestion media type is unknown.");
            }

            if (double.IsNaN(suggestion.Confidence) || suggestion.Confidence < 0 || suggestion.Confidence > 1)
            {
                return LlmSuggestionValidationResult.Failed("LLM suggestion confidence is out of range.");
            }

            if (suggestion.Confidence < confidenceThreshold)
            {
                return LlmSuggestionValidationResult.Failed("LLM suggestion confidence is below threshold.");
            }

            if (suggestion.Year.HasValue && !IsValidYear(suggestion.Year.Value))
            {
                return LlmSuggestionValidationResult.Failed("LLM suggestion year is invalid.");
            }

            if (suggestion.SeasonNumber is < 0 || suggestion.EpisodeNumber is < 0)
            {
                return LlmSuggestionValidationResult.Failed("LLM suggestion season or episode number is invalid.");
            }

            if (IsTooLong(suggestion.Title, MaxTitleLength)
                || IsTooLong(suggestion.OriginalTitle, MaxTitleLength)
                || IsTooLong(suggestion.Overview, MaxOverviewLength))
            {
                return LlmSuggestionValidationResult.Failed("LLM suggestion field is too long.");
            }

            if (string.IsNullOrWhiteSpace(suggestion.Title)
                && !suggestion.Year.HasValue
                && !suggestion.SeasonNumber.HasValue
                && !suggestion.EpisodeNumber.HasValue
                && string.IsNullOrWhiteSpace(suggestion.OriginalTitle)
                && string.IsNullOrWhiteSpace(suggestion.Overview))
            {
                return LlmSuggestionValidationResult.Failed("LLM suggestion is empty.");
            }

            return LlmSuggestionValidationResult.Succeeded(suggestion);
        }

        private static string? NormalizeText(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static bool IsValidYear(int year)
        {
            return year >= 1874 && year <= DateTime.UtcNow.Year + 2;
        }

        private static bool IsTooLong(string? value, int maxLength)
        {
            return value != null && value.Length > maxLength;
        }

        private static string? GetOptionalString(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (property.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException($"LLM suggestion schema invalid: field '{propertyName}' must be string.");
            }

            return property.GetString();
        }

        private static int? GetOptionalInt(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var value))
            {
                throw new InvalidOperationException($"LLM suggestion schema invalid: field '{propertyName}' must be integer.");
            }

            return value;
        }

        private static double GetRequiredDouble(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var property))
            {
                throw new InvalidOperationException($"LLM suggestion schema invalid: field '{propertyName}' is required.");
            }

            if (property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out var value))
            {
                throw new InvalidOperationException($"LLM suggestion schema invalid: field '{propertyName}' must be number.");
            }

            return value;
        }

        private static LlmSuggestionValidationResult ValidateSuggestionsEnvelopeFields(JsonElement root)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, "suggestions", StringComparison.Ordinal))
                {
                    return LlmSuggestionValidationResult.Failed($"LLM suggestion schema invalid: top-level field '{property.Name}' is not allowed.");
                }
            }

            return LlmSuggestionValidationResult.NoCandidate("EnvelopeFieldsValid");
        }

        private static bool IsKnownSuggestionField(string fieldName)
        {
            return string.Equals(fieldName, "mediaType", StringComparison.Ordinal)
                || string.Equals(fieldName, "title", StringComparison.Ordinal)
                || string.Equals(fieldName, "year", StringComparison.Ordinal)
                || string.Equals(fieldName, "seasonNumber", StringComparison.Ordinal)
                || string.Equals(fieldName, "episodeNumber", StringComparison.Ordinal)
                || string.Equals(fieldName, "originalTitle", StringComparison.Ordinal)
                || string.Equals(fieldName, "overview", StringComparison.Ordinal)
                || string.Equals(fieldName, "confidence", StringComparison.Ordinal);
        }

        private LlmSuggestionValidationResult ParseAndValidateSuggestions(JsonElement suggestions, double confidenceThreshold)
        {
            if (suggestions.ValueKind != JsonValueKind.Array)
            {
                return LlmSuggestionValidationResult.Failed("LLM suggestion schema invalid: field 'suggestions' must be array.");
            }

            foreach (var suggestionElement in suggestions.EnumerateArray())
            {
                if (suggestionElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var result = this.ParseAndValidateSuggestion(suggestionElement, confidenceThreshold);
                if (result.Success && result.Suggestion != null)
                {
                    return result;
                }
            }

            return LlmSuggestionValidationResult.NoCandidate("NoCandidate");
        }

        private LlmSuggestionValidationResult ParseAndValidateSuggestion(JsonElement root, double confidenceThreshold)
        {
            try
            {
                this.LogUnknownSuggestionFields(root);

                var suggestion = new LlmScrapingSuggestion
                {
                    MediaType = GetOptionalString(root, "mediaType"),
                    Title = GetOptionalString(root, "title"),
                    Year = GetOptionalInt(root, "year"),
                    SeasonNumber = GetOptionalInt(root, "seasonNumber"),
                    EpisodeNumber = GetOptionalInt(root, "episodeNumber"),
                    OriginalTitle = GetOptionalString(root, "originalTitle"),
                    Overview = GetOptionalString(root, "overview"),
                    Confidence = GetRequiredDouble(root, "confidence"),
                };

                return this.Validate(suggestion, confidenceThreshold);
            }
            catch (InvalidOperationException ex)
            {
                return LlmSuggestionValidationResult.Failed(ex.Message);
            }
        }

        private void LogUnknownSuggestionFields(JsonElement root)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (!IsKnownSuggestionField(property.Name))
                {
                    LogIgnoredUnknownSuggestionField(this.logger, property.Name, null);
                }
            }
        }
    }
}
