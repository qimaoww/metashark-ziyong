// <copyright file="EpisodeGroupMapParser.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;
    using System.Linq;

    public sealed class EpisodeGroupMapParser
    {
        private readonly StringComparer seriesIdComparer = StringComparer.OrdinalIgnoreCase;

        public static EpisodeGroupMapParser Shared { get; } = new EpisodeGroupMapParser();

        public EpisodeGroupMapSnapshot ParseSnapshot(string? mapping)
        {
            var groupIdsBySeriesId = new Dictionary<string, string>(this.seriesIdComparer);
            var invalidWarnings = new List<string>();
            var duplicateWarnings = new List<string>();

            if (!string.IsNullOrWhiteSpace(mapping))
            {
                using var reader = new StringReader(mapping);
                string? line;
                var lineNumber = 0;

                while ((line = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    var trimmedLine = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith('#'))
                    {
                        continue;
                    }

                    var separatorIndex = trimmedLine.IndexOf('=', StringComparison.Ordinal);
                    if (separatorIndex < 0)
                    {
                        invalidWarnings.Add($"Invalid mapping at line {lineNumber}: missing '=' separator.");
                        continue;
                    }

                    var seriesId = trimmedLine[..separatorIndex].Trim();
                    var groupId = trimmedLine[(separatorIndex + 1)..].Trim();
                    if (string.IsNullOrEmpty(seriesId) || string.IsNullOrEmpty(groupId))
                    {
                        invalidWarnings.Add($"Invalid mapping at line {lineNumber}: {GetEmptyFieldReason(seriesId, groupId)}");
                        continue;
                    }

                    if (groupIdsBySeriesId.ContainsKey(seriesId))
                    {
                        duplicateWarnings.Add($"Duplicate mapping at line {lineNumber}: seriesId '{seriesId}' keeps the first valid group id.");
                        continue;
                    }

                    groupIdsBySeriesId.Add(seriesId, groupId);
                }
            }

            var orderedEntries = groupIdsBySeriesId
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .ToArray();
            var canonicalText = string.Join("\n", orderedEntries.Select(entry => $"{entry.Key}={entry.Value}"));
            var mappedSeriesIds = orderedEntries.Select(entry => entry.Key).ToArray();

            return new EpisodeGroupMapSnapshot(
                new ReadOnlyDictionary<string, string>(groupIdsBySeriesId),
                mappedSeriesIds,
                invalidWarnings.ToArray(),
                duplicateWarnings.ToArray(),
                canonicalText);
        }

        public string GetCanonicalText(string? mapping)
        {
            return this.ParseSnapshot(mapping).CanonicalText;
        }

        public IReadOnlyCollection<string> GetMappedSeriesIds(string? mapping)
        {
            return this.ParseSnapshot(mapping).MappedSeriesIds;
        }

        public bool TryGetGroupId(string? mapping, string? tmdbSeriesId, out string groupId)
        {
            return this.ParseSnapshot(mapping).TryGetGroupId(tmdbSeriesId, out groupId);
        }

        private static string GetEmptyFieldReason(string seriesId, string groupId)
        {
            if (string.IsNullOrEmpty(seriesId) && string.IsNullOrEmpty(groupId))
            {
                return "empty key and value.";
            }

            if (string.IsNullOrEmpty(seriesId))
            {
                return "empty key.";
            }

            return "empty value.";
        }
    }
}
