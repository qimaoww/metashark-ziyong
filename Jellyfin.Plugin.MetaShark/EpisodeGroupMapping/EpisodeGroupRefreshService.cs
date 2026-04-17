// <copyright file="EpisodeGroupRefreshService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    using System;
    using System.Collections.Generic;

    public sealed class EpisodeGroupRefreshService
    {
        private static readonly StringComparer SeriesIdComparer = StringComparer.OrdinalIgnoreCase;

        private readonly EpisodeGroupMapParser parser;

        public EpisodeGroupRefreshService(EpisodeGroupMapParser? parser = null)
        {
            this.parser = parser ?? EpisodeGroupMapParser.Shared;
        }

        public static EpisodeGroupRefreshResult CreateRefreshResult(EpisodeGroupMapSnapshot? oldSnapshot, EpisodeGroupMapSnapshot? newSnapshot)
        {
            var resolvedOldSnapshot = oldSnapshot ?? EpisodeGroupMapSnapshot.Empty;
            var resolvedNewSnapshot = newSnapshot ?? EpisodeGroupMapSnapshot.Empty;

            var addedSeriesIds = new HashSet<string>(SeriesIdComparer);
            var removedSeriesIds = new HashSet<string>(SeriesIdComparer);
            var changedSeriesIds = new HashSet<string>(SeriesIdComparer);

            foreach (var newEntry in resolvedNewSnapshot.GroupIdsBySeriesId)
            {
                if (!resolvedOldSnapshot.GroupIdsBySeriesId.TryGetValue(newEntry.Key, out var oldGroupId))
                {
                    addedSeriesIds.Add(newEntry.Key);
                    continue;
                }

                if (!string.Equals(oldGroupId, newEntry.Value, StringComparison.Ordinal))
                {
                    changedSeriesIds.Add(newEntry.Key);
                }
            }

            foreach (var oldEntry in resolvedOldSnapshot.GroupIdsBySeriesId)
            {
                if (!resolvedNewSnapshot.GroupIdsBySeriesId.ContainsKey(oldEntry.Key))
                {
                    removedSeriesIds.Add(oldEntry.Key);
                }
            }

            return new EpisodeGroupRefreshResult(
                resolvedOldSnapshot,
                resolvedNewSnapshot,
                addedSeriesIds,
                removedSeriesIds,
                changedSeriesIds);
        }

        public EpisodeGroupMapSnapshot ParseSnapshot(string? mapping)
        {
            return this.parser.ParseSnapshot(mapping);
        }

        public EpisodeGroupRefreshResult CreateRefreshResult(string? oldMapping, string? newMapping)
        {
            var oldSnapshot = this.parser.ParseSnapshot(oldMapping);
            var newSnapshot = this.parser.ParseSnapshot(newMapping);
            return EpisodeGroupRefreshService.CreateRefreshResult(oldSnapshot, newSnapshot);
        }
    }
}
