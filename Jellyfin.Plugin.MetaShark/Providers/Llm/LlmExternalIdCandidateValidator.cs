// <copyright file="LlmExternalIdCandidateValidator.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;
    using System.Text.Json;
    using System.Text.RegularExpressions;

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Validator is intentionally injectable for future resolver composition.")]
    [SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1204:Static elements should appear before instance elements", Justification = "Keep validation flow before helper methods.")]
    public sealed class LlmExternalIdCandidateValidator
    {
        private const int MaxProviderLength = 32;
        private const int MaxIdLength = 64;
        private const int MaxMediaTypeLength = 32;
        private const int MaxReasonLength = 500;
        private const int MaxEvidenceLength = 1000;

        private static readonly HashSet<string> AllowedCandidateFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "provider",
            "id",
            "mediaType",
            "confidence",
            "reason",
            "evidence",
        };

        private static readonly HashSet<string> AllowedResponseFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "externalIdCandidates",
        };

        private static readonly string[] EmptyExternalIdCandidateDiagnostics =
        {
            "LLM external ID response contains no candidates.",
        };

        public LlmExternalIdCandidateValidationResult ParseAndValidate(string? contentJson, double confidenceThreshold)
        {
            return this.ParseAndValidateResponse(contentJson, confidenceThreshold);
        }

        public LlmExternalIdCandidateValidationResult ParseAndValidateResponse(string? contentJson, double confidenceThreshold)
        {
            if (string.IsNullOrWhiteSpace(contentJson))
            {
                return LlmExternalIdCandidateValidationResult.Failed("LLM external ID candidate JSON is empty.");
            }

            try
            {
                using var document = JsonDocument.Parse(contentJson);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return LlmExternalIdCandidateValidationResult.Failed("LLM external ID candidate schema invalid.");
                }

                if (root.TryGetProperty("externalIdCandidates", out var externalIdCandidatesElement))
                {
                    return this.ParseCandidateResponse(root, externalIdCandidatesElement, confidenceThreshold);
                }

                return this.ParseCandidateObject(root, confidenceThreshold);
            }
            catch (JsonException ex)
            {
                return LlmExternalIdCandidateValidationResult.Failed($"LLM external ID candidate schema invalid: {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                return LlmExternalIdCandidateValidationResult.Failed(ex.Message);
            }
        }

        public LlmExternalIdCandidateValidationResult Validate(LlmExternalIdCandidate? candidate, double confidenceThreshold)
        {
            if (candidate == null)
            {
                return LlmExternalIdCandidateValidationResult.Failed("LLM external ID candidate is empty.");
            }

            candidate.Provider = NormalizeProvider(candidate.Provider);
            candidate.Id = NormalizeId(candidate.Provider, candidate.Id);
            candidate.MediaType = NormalizeMediaType(candidate.MediaType);
            candidate.Reason = NormalizeText(candidate.Reason);
            candidate.Evidence = NormalizeText(candidate.Evidence);

            if (string.IsNullOrWhiteSpace(candidate.Provider))
            {
                return LlmExternalIdCandidateValidationResult.Failed("LLM external ID candidate provider is required.");
            }

            if (string.IsNullOrWhiteSpace(candidate.Id))
            {
                return LlmExternalIdCandidateValidationResult.Failed("LLM external ID candidate id is required.");
            }

            if (string.IsNullOrWhiteSpace(candidate.MediaType))
            {
                return LlmExternalIdCandidateValidationResult.Failed("LLM external ID candidate media type is required.");
            }

            if (string.IsNullOrWhiteSpace(candidate.Reason))
            {
                return LlmExternalIdCandidateValidationResult.Failed("LLM external ID candidate reason is required.");
            }

            if (string.IsNullOrWhiteSpace(candidate.Evidence))
            {
                return LlmExternalIdCandidateValidationResult.Failed("LLM external ID candidate evidence is required.");
            }

            if (IsTooLong(candidate.Provider, MaxProviderLength)
                || IsTooLong(candidate.Id, MaxIdLength)
                || IsTooLong(candidate.MediaType, MaxMediaTypeLength)
                || IsTooLong(candidate.Reason, MaxReasonLength)
                || IsTooLong(candidate.Evidence, MaxEvidenceLength))
            {
                return LlmExternalIdCandidateValidationResult.Failed("LLM external ID candidate field is too long.");
            }

            if (!IsKnownProvider(candidate.Provider))
            {
                return LlmExternalIdCandidateValidationResult.Failed("LLM external ID candidate provider is unknown.");
            }

            if (!IsKnownMediaType(candidate.MediaType))
            {
                return LlmExternalIdCandidateValidationResult.Failed("LLM external ID candidate media type is unknown.");
            }

            if (double.IsNaN(candidate.Confidence) || candidate.Confidence < 0 || candidate.Confidence > 1)
            {
                return LlmExternalIdCandidateValidationResult.Failed("LLM external ID candidate confidence is out of range.");
            }

            if (candidate.Confidence < confidenceThreshold)
            {
                return LlmExternalIdCandidateValidationResult.Failed("LLM external ID candidate confidence is below threshold.");
            }

            if (!IsProviderAllowedForMediaType(candidate.Provider, candidate.MediaType))
            {
                return LlmExternalIdCandidateValidationResult.Failed("LLM external ID candidate provider is not allowed for media type.");
            }

            if (!HasValidProviderIdFormat(candidate.Provider, candidate.Id))
            {
                return LlmExternalIdCandidateValidationResult.Failed("LLM external ID candidate id format is invalid.");
            }

            return LlmExternalIdCandidateValidationResult.Succeeded(candidate);
        }

        private static string? NormalizeText(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string? NormalizeProvider(string? value)
        {
            var normalized = NormalizeText(value);
            if (normalized == null)
            {
                return null;
            }

            return normalized.ToUpperInvariant() switch
            {
                "TMDB" => "TMDb",
                "TVDB" => "TVDB",
                "DOUBAN" => "Douban",
                "IMDB" => "IMDb",
                _ => normalized,
            };
        }

        private static string? NormalizeMediaType(string? value)
        {
            var normalized = NormalizeText(value);
            if (normalized == null)
            {
                return null;
            }

            return normalized.ToUpperInvariant() switch
            {
                "MOVIE" => "Movie",
                "SERIES" => "Series",
                "SEASON" => "Season",
                "EPISODE" => "Episode",
                _ => normalized,
            };
        }

        private static string? NormalizeId(string? provider, string? value)
        {
            var normalized = NormalizeText(value);
            if (normalized == null)
            {
                return null;
            }

            return string.Equals(NormalizeProvider(provider), "IMDb", StringComparison.Ordinal) && normalized.StartsWith("tt", StringComparison.OrdinalIgnoreCase)
                ? "tt" + normalized[2..]
                : normalized;
        }

        private static bool IsKnownProvider(string provider)
        {
            return string.Equals(provider, "TMDb", StringComparison.Ordinal)
                || string.Equals(provider, "TVDB", StringComparison.Ordinal)
                || string.Equals(provider, "Douban", StringComparison.Ordinal)
                || string.Equals(provider, "IMDb", StringComparison.Ordinal);
        }

        private static bool IsKnownMediaType(string mediaType)
        {
            return string.Equals(mediaType, "Movie", StringComparison.Ordinal)
                || string.Equals(mediaType, "Series", StringComparison.Ordinal)
                || string.Equals(mediaType, "Season", StringComparison.Ordinal)
                || string.Equals(mediaType, "Episode", StringComparison.Ordinal);
        }

        private static bool IsProviderAllowedForMediaType(string provider, string mediaType)
        {
            if (string.Equals(mediaType, "Season", StringComparison.Ordinal))
            {
                return false;
            }

            if (string.Equals(mediaType, "Movie", StringComparison.Ordinal))
            {
                return string.Equals(provider, "TMDb", StringComparison.Ordinal)
                    || string.Equals(provider, "IMDb", StringComparison.Ordinal)
                    || string.Equals(provider, "Douban", StringComparison.Ordinal);
            }

            if (string.Equals(mediaType, "Series", StringComparison.Ordinal))
            {
                return string.Equals(provider, "TMDb", StringComparison.Ordinal)
                    || string.Equals(provider, "IMDb", StringComparison.Ordinal)
                    || string.Equals(provider, "TVDB", StringComparison.Ordinal)
                    || string.Equals(provider, "Douban", StringComparison.Ordinal);
            }

            if (string.Equals(mediaType, "Episode", StringComparison.Ordinal))
            {
                return string.Equals(provider, "TMDb", StringComparison.Ordinal)
                    || string.Equals(provider, "TVDB", StringComparison.Ordinal);
            }

            return false;
        }

        private static bool HasValidProviderIdFormat(string provider, string id)
        {
            if (string.Equals(provider, "IMDb", StringComparison.Ordinal))
            {
                return Regex.IsMatch(id, @"^tt\d{7,}$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            }

            return Regex.IsMatch(id, @"^\d+$", RegexOptions.None, TimeSpan.FromMilliseconds(100))
                && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                && value > 0;
        }

        private static bool IsTooLong(string? value, int maxLength)
        {
            return value != null && value.Length > maxLength;
        }

        private static string GetRequiredString(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var property))
            {
                throw new InvalidOperationException($"LLM external ID candidate schema invalid: field '{propertyName}' is required.");
            }

            if (property.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException($"LLM external ID candidate schema invalid: field '{propertyName}' must be string.");
            }

            return property.GetString()!;
        }

        private static double GetRequiredDouble(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var property))
            {
                throw new InvalidOperationException($"LLM external ID candidate schema invalid: field '{propertyName}' is required.");
            }

            if (property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out var value))
            {
                throw new InvalidOperationException($"LLM external ID candidate schema invalid: field '{propertyName}' must be number.");
            }

            return value;
        }

        private LlmExternalIdCandidateValidationResult ParseCandidateResponse(JsonElement root, JsonElement externalIdCandidatesElement, double confidenceThreshold)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (!AllowedResponseFields.Contains(property.Name))
                {
                    return LlmExternalIdCandidateValidationResult.Failed($"LLM external ID response schema invalid: field '{property.Name}' is not allowed.");
                }
            }

            if (externalIdCandidatesElement.ValueKind != JsonValueKind.Array)
            {
                return LlmExternalIdCandidateValidationResult.Failed("LLM external ID response schema invalid: field 'externalIdCandidates' must be array.");
            }

            var accepted = new List<LlmExternalIdCandidate>();
            var diagnostics = new List<string>();
            foreach (var element in externalIdCandidatesElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    diagnostics.Add("LLM external ID candidate schema invalid.");
                    continue;
                }

                var result = this.ParseCandidateObject(element, confidenceThreshold);
                if (result.Success)
                {
                    accepted.Add(result.Candidate!);
                }
                else
                {
                    diagnostics.Add(result.Diagnostic);
                }
            }

            if (accepted.Count == 0)
            {
                return diagnostics.Count == 0
                    ? LlmExternalIdCandidateValidationResult.Succeeded(accepted, EmptyExternalIdCandidateDiagnostics)
                    : LlmExternalIdCandidateValidationResult.Failed(string.Join(" ", diagnostics));
            }

            return LlmExternalIdCandidateValidationResult.Succeeded(accepted, diagnostics);
        }

        private LlmExternalIdCandidateValidationResult ParseCandidateObject(JsonElement root, double confidenceThreshold)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (!AllowedCandidateFields.Contains(property.Name))
                {
                    return LlmExternalIdCandidateValidationResult.Failed($"LLM external ID candidate schema invalid: field '{property.Name}' is not allowed.");
                }
            }

            var candidate = new LlmExternalIdCandidate
            {
                Provider = GetRequiredString(root, "provider"),
                Id = GetRequiredString(root, "id"),
                MediaType = GetRequiredString(root, "mediaType"),
                Confidence = GetRequiredDouble(root, "confidence"),
                Reason = GetRequiredString(root, "reason"),
                Evidence = GetRequiredString(root, "evidence"),
            };

            return this.Validate(candidate, confidenceThreshold);
        }
    }
}
