// <copyright file="LlmTmdbCorrectionMapParser.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.IO;
    using System.Linq;

    public sealed class LlmTmdbCorrectionMapParser
    {
        private const string DoubanProviderKey = "douban";
        private const string TmdbProviderKey = "tmdb";
        private static readonly StringComparer EntryComparer = StringComparer.OrdinalIgnoreCase;

        public static LlmTmdbCorrectionMapParser Shared { get; } = new LlmTmdbCorrectionMapParser();

        [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Parser stays instance-based because persistence services receive it as a composable dependency.")]
        public LlmTmdbCorrectionMapSnapshot ParseSnapshot(string? mapping)
        {
            var entriesByKey = new Dictionary<string, LlmTmdbCorrectionMapEntry>(EntryComparer);
            if (!string.IsNullOrWhiteSpace(mapping))
            {
                using var reader = new StringReader(mapping);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    var trimmedLine = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith('#'))
                    {
                        continue;
                    }

                    var separatorIndex = trimmedLine.IndexOf('=', StringComparison.Ordinal);
                    if (separatorIndex < 0)
                    {
                        continue;
                    }

                    var key = NormalizeToken(trimmedLine[..separatorIndex]);
                    var value = NormalizeToken(trimmedLine[(separatorIndex + 1)..]);
                    if (!TryParseEntry(key, value, out var entry) || entriesByKey.ContainsKey(entry.Key))
                    {
                        continue;
                    }

                    entriesByKey.Add(entry.Key, entry);
                }
            }

            var canonicalText = string.Join(
                "\n",
                entriesByKey.Values
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => $"{entry.Key}={entry.Value}"));
            return new LlmTmdbCorrectionMapSnapshot(entriesByKey, canonicalText);
        }

        public string GetCanonicalText(string? mapping)
        {
            return this.ParseSnapshot(mapping).CanonicalText;
        }

        public bool TryGetDoubanCorrection(string? mapping, string mediaType, string? doubanId, out string tmdbId)
        {
            return this.ParseSnapshot(mapping).TryGetDoubanCorrection(mediaType, doubanId, out tmdbId);
        }

        public string UpsertDoubanCorrection(string? mapping, string mediaType, string doubanId, string tmdbId)
        {
            if (!TryCreateDoubanEntry(mediaType, doubanId, tmdbId, out var newEntry))
            {
                return this.GetCanonicalText(mapping);
            }

            var snapshot = this.ParseSnapshot(mapping);
            var entries = snapshot.EntriesByKey.Values.ToDictionary(entry => entry.Key, entry => entry, EntryComparer);
            entries[newEntry.Key] = newEntry;
            return string.Join(
                "\n",
                entries.Values
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => $"{entry.Key}={entry.Value}"));
        }

        internal static bool TryBuildDoubanKey(string mediaType, string? doubanId, out string key)
        {
            key = string.Empty;
            var normalizedMediaType = NormalizeMediaType(mediaType);
            var normalizedDoubanId = NormalizeToken(doubanId);
            if (string.IsNullOrEmpty(normalizedMediaType) || string.IsNullOrEmpty(normalizedDoubanId))
            {
                return false;
            }

            key = $"{normalizedMediaType}:{DoubanProviderKey}:{normalizedDoubanId}";
            return true;
        }

        private static bool TryParseEntry(string key, string value, out LlmTmdbCorrectionMapEntry entry)
        {
            entry = null!;
            var keyParts = key.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var valueParts = value.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (keyParts.Length != 3
                || valueParts.Length != 2
                || !string.Equals(keyParts[1], DoubanProviderKey, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(valueParts[0], TmdbProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return TryCreateDoubanEntry(keyParts[0], keyParts[2], valueParts[1], out entry);
        }

        private static bool TryCreateDoubanEntry(string mediaType, string doubanId, string tmdbId, out LlmTmdbCorrectionMapEntry entry)
        {
            entry = null!;
            var normalizedMediaType = NormalizeMediaType(mediaType);
            var normalizedDoubanId = NormalizeToken(doubanId);
            var normalizedTmdbId = NormalizeToken(tmdbId);
            if (string.IsNullOrEmpty(normalizedMediaType)
                || string.IsNullOrEmpty(normalizedDoubanId)
                || string.IsNullOrEmpty(normalizedTmdbId)
                || !int.TryParse(normalizedTmdbId, out var parsedTmdbId)
                || parsedTmdbId <= 0)
            {
                return false;
            }

            entry = new LlmTmdbCorrectionMapEntry(normalizedMediaType, DoubanProviderKey, normalizedDoubanId, normalizedTmdbId);
            return true;
        }

        private static string NormalizeMediaType(string? mediaType)
        {
            var normalized = NormalizeToken(mediaType);
            return normalized switch
            {
                "movie" => "movie",
                "series" => "series",
                _ => string.Empty,
            };
        }

        [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Persisted correction-map canonical text is intentionally lowercase for compatibility.")]
        private static string NormalizeToken(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
        }
    }
}
