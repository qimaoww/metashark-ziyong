// <copyright file="LlmSuggestionValidator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Text.Json;

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Validator is intentionally injectable for assist service composition tests.")]
    public sealed class LlmSuggestionValidator
    {
        private const int MaxTitleLength = 200;
        private const int MaxOverviewLength = 4000;

        private static readonly HashSet<string> AllowedMediaTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Movie",
            "Series",
            "Season",
            "Episode",
        };

        private static readonly HashSet<string> AllowedJsonFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "mediaType",
            "title",
            "year",
            "seasonNumber",
            "episodeNumber",
            "originalTitle",
            "overview",
            "confidence",
        };

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

                foreach (var property in root.EnumerateObject())
                {
                    if (!AllowedJsonFields.Contains(property.Name))
                    {
                        return LlmSuggestionValidationResult.Failed($"LLM suggestion schema invalid: field '{property.Name}' is not allowed.");
                    }
                }

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
            catch (JsonException ex)
            {
                return LlmSuggestionValidationResult.Failed($"LLM suggestion schema invalid: {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                return LlmSuggestionValidationResult.Failed(ex.Message);
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
    }
}
