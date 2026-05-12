// <copyright file="TmdbEpisodeGroupMapping.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Jellyfin.Plugin.MetaShark.EpisodeGroupMapping;

    public static class TmdbEpisodeGroupMapping
    {
        public static bool TryGetGroupId(string? mapping, string? tmdbSeriesId, out string groupId)
        {
            return EpisodeGroupMapParser.Shared.TryGetGroupId(mapping, tmdbSeriesId, out groupId);
        }

        public static bool TryGetGroupId(string? manualMapping, string? llmMapping, string? tmdbSeriesId, out string groupId)
        {
            if (EpisodeGroupMapParser.Shared.TryGetGroupId(manualMapping, tmdbSeriesId, out groupId))
            {
                return true;
            }

            return EpisodeGroupMapParser.Shared.TryGetGroupId(llmMapping, tmdbSeriesId, out groupId);
        }

        public static string GetEffectiveMappingText(string? manualMapping, string? llmMapping)
        {
            var parser = EpisodeGroupMapParser.Shared;
            var llmSnapshot = parser.ParseSnapshot(llmMapping);
            var manualSnapshot = parser.ParseSnapshot(manualMapping);
            var entries = new Dictionary<string, string>(llmSnapshot.GroupIdsBySeriesId, StringComparer.OrdinalIgnoreCase);

            foreach (var entry in manualSnapshot.GroupIdsBySeriesId)
            {
                entries[entry.Key] = entry.Value;
            }

            return string.Join(
                "\n",
                entries
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => $"{entry.Key}={entry.Value}"));
        }
    }
}
