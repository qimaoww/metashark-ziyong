// <copyright file="LlmScrapeMismatchDetector.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill;

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Detector is intentionally injectable for assist service composition tests.")]
    public sealed class LlmScrapeMismatchDetector
    {
        private static readonly Regex LatinTokenRegex = new Regex("[a-z0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex CjkTokenRegex = new Regex(@"\p{IsCJKUnifiedIdeographs}+|\p{IsHiragana}+|\p{IsKatakana}+", RegexOptions.Compiled);

        private static readonly HashSet<string> NoiseTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the",
            "a",
            "an",
            "of",
            "and",
            "movie",
            "series",
            "season",
            "episode",
            "ep",
            "ova",
            "sp",
            "s",
            "e",
        };

        public LlmMismatchResult Detect(LlmPromptContext? context, LlmScrapingSuggestion? suggestion)
        {
            if (context == null || suggestion == null)
            {
                return LlmMismatchResult.NotMismatched("InputMissing");
            }

            if (HasMediaStructureMismatch(context.MediaType, suggestion.MediaType))
            {
                return LlmMismatchResult.Mismatched("MediaStructureMismatch");
            }

            if (HasYearMismatch(context, suggestion))
            {
                return LlmMismatchResult.Mismatched("YearMismatch");
            }

            if (HasSeasonEpisodeMismatch(context, suggestion))
            {
                return LlmMismatchResult.Mismatched("SeasonEpisodeMismatch");
            }

            if (HasTitleTokenMismatch(context, suggestion))
            {
                return LlmMismatchResult.Mismatched("TitleTokensDisjoint");
            }

            return LlmMismatchResult.NotMismatched("NoDeterministicMismatch");
        }

        public bool IsMismatch(LlmPromptContext? context, LlmScrapingSuggestion? suggestion)
        {
            return this.Detect(context, suggestion).IsMismatch;
        }

        private static bool HasMediaStructureMismatch(string? contextMediaType, string? suggestionMediaType)
        {
            var contextType = NormalizeText(contextMediaType);
            var suggestionType = NormalizeText(suggestionMediaType);
            if (contextType == null || suggestionType == null)
            {
                return false;
            }

            if (string.Equals(contextType, suggestionType, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return IsMovieLike(contextType) != IsMovieLike(suggestionType)
                || IsTvLike(contextType) != IsTvLike(suggestionType);
        }

        private static bool HasTitleTokenMismatch(LlmPromptContext context, LlmScrapingSuggestion suggestion)
        {
            if (IsDefaultEpisodeTitleContext(context))
            {
                return false;
            }

            var contextTokens = BuildContextTitleTokens(context);
            var suggestionTokens = BuildSuggestionTitleTokens(suggestion);
            if (contextTokens.Count == 0 || suggestionTokens.Count == 0)
            {
                return false;
            }

            return !contextTokens.Overlaps(suggestionTokens);
        }

        private static bool HasYearMismatch(LlmPromptContext context, LlmScrapingSuggestion suggestion)
        {
            var contextYear = context.ParsedYear ?? context.Year;
            return contextYear.HasValue && suggestion.Year.HasValue && Math.Abs(contextYear.Value - suggestion.Year.Value) > 1;
        }

        private static bool HasSeasonEpisodeMismatch(LlmPromptContext context, LlmScrapingSuggestion suggestion)
        {
            var contextSeasonNumber = context.ParsedSeasonNumber ?? context.SeasonNumber;
            var contextEpisodeNumber = context.ParsedEpisodeNumber ?? context.EpisodeNumber;
            return (contextSeasonNumber.HasValue && suggestion.SeasonNumber.HasValue && contextSeasonNumber.Value != suggestion.SeasonNumber.Value)
                || (contextEpisodeNumber.HasValue && suggestion.EpisodeNumber.HasValue && contextEpisodeNumber.Value != suggestion.EpisodeNumber.Value);
        }

        private static HashSet<string> BuildContextTitleTokens(LlmPromptContext context)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!IsDefaultEpisodeTitleContext(context))
            {
                AddTitleTokens(tokens, context.Name);
            }

            AddTitleTokens(tokens, context.ParsedName);
            AddTitleTokens(tokens, context.ParsedChineseName);
            AddTitleTokens(tokens, context.ParentFolderName);
            return tokens;
        }

        private static HashSet<string> BuildSuggestionTitleTokens(LlmScrapingSuggestion suggestion)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddTitleTokens(tokens, suggestion.Title);
            AddTitleTokens(tokens, suggestion.OriginalTitle);
            return tokens;
        }

        private static void AddTitleTokens(HashSet<string> tokens, string? value)
        {
            var normalized = NormalizeForTokenization(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            foreach (Match match in LatinTokenRegex.Matches(normalized))
            {
                var token = match.Value.ToUpperInvariant();
                if (token.Length >= 3 && !NoiseTokens.Contains(token) && !token.All(char.IsDigit))
                {
                    tokens.Add(token);
                }
            }

            foreach (Match match in CjkTokenRegex.Matches(normalized))
            {
                foreach (var token in CreateCjkTokens(match.Value))
                {
                    tokens.Add(token);
                }
            }
        }

        private static IEnumerable<string> CreateCjkTokens(string value)
        {
            if (value.Length == 1)
            {
                yield break;
            }

            if (value.Length <= 4)
            {
                yield return value;
                yield break;
            }

            for (var index = 0; index <= value.Length - 2; index++)
            {
                yield return value.Substring(index, 2);
            }
        }

        private static string NormalizeForTokenization(string? value)
        {
            var normalized = NormalizeText(value);
            if (normalized == null)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(normalized.Length);
            foreach (var character in normalized.Normalize(NormalizationForm.FormKC))
            {
                if (char.IsLetterOrDigit(character) || IsCjk(character))
                {
                    builder.Append(char.ToLower(character, CultureInfo.InvariantCulture));
                }
                else
                {
                    builder.Append(' ');
                }
            }

            return builder.ToString();
        }

        private static bool IsCjk(char character)
        {
            return (character >= '\u4e00' && character <= '\u9fff')
                || (character >= '\u3040' && character <= '\u30ff');
        }

        private static bool IsDefaultEpisodeTitleContext(LlmPromptContext context)
        {
            return string.Equals(context.MediaType, "Episode", StringComparison.OrdinalIgnoreCase)
                && EpisodeTitleBackfillPolicy.IsDefaultJellyfinEpisodeTitle(context.Name);
        }

        private static bool IsMovieLike(string mediaType)
        {
            return string.Equals(mediaType, "Movie", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTvLike(string mediaType)
        {
            return string.Equals(mediaType, "Series", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mediaType, "Season", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mediaType, "Episode", StringComparison.OrdinalIgnoreCase);
        }

        private static string? NormalizeText(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
