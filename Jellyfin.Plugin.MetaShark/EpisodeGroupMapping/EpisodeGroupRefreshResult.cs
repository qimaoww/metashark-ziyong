// <copyright file="EpisodeGroupRefreshResult.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;

    public sealed class EpisodeGroupRefreshResult
    {
        public EpisodeGroupRefreshResult(
            EpisodeGroupMapSnapshot? oldSnapshot,
            EpisodeGroupMapSnapshot? newSnapshot,
            IReadOnlyCollection<string>? addedSeriesIds = null,
            IReadOnlyCollection<string>? removedSeriesIds = null,
            IReadOnlyCollection<string>? changedSeriesIds = null)
        {
            this.OldSnapshot = oldSnapshot ?? EpisodeGroupMapSnapshot.Empty;
            this.NewSnapshot = newSnapshot ?? EpisodeGroupMapSnapshot.Empty;
            this.AddedSeriesIds = NormalizeSeriesIds(addedSeriesIds);
            this.RemovedSeriesIds = NormalizeSeriesIds(removedSeriesIds);
            this.ChangedSeriesIds = NormalizeSeriesIds(changedSeriesIds);
            this.AffectedSeriesIds = NormalizeSeriesIds(this.AddedSeriesIds.Concat(this.RemovedSeriesIds).Concat(this.ChangedSeriesIds));
        }

        public EpisodeGroupMapSnapshot OldSnapshot { get; }

        public EpisodeGroupMapSnapshot NewSnapshot { get; }

        public IReadOnlyCollection<string> AddedSeriesIds { get; }

        public IReadOnlyCollection<string> RemovedSeriesIds { get; }

        public IReadOnlyCollection<string> ChangedSeriesIds { get; }

        public IReadOnlyCollection<string> AffectedSeriesIds { get; }

        public bool HasChanges => !this.IsNoOp;

        public bool IsNoOp => this.AffectedSeriesIds.Count == 0;

        public int OldInvalidWarningCount => this.OldSnapshot.InvalidWarnings.Count;

        public int NewInvalidWarningCount => this.NewSnapshot.InvalidWarnings.Count;

        public int OldDuplicateWarningCount => this.OldSnapshot.DuplicateWarnings.Count;

        public int NewDuplicateWarningCount => this.NewSnapshot.DuplicateWarnings.Count;

        public string CreateSummaryMessage(int queuedSeriesCount)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Episode group refresh summary: queued={0}, affected={1} (added={2}, removed={3}, changed={4}), invalid(old/new)={5}/{6}, duplicate(old/new)={7}/{8}, no-op={9}.",
                queuedSeriesCount,
                this.AffectedSeriesIds.Count,
                this.AddedSeriesIds.Count,
                this.RemovedSeriesIds.Count,
                this.ChangedSeriesIds.Count,
                this.OldInvalidWarningCount,
                this.NewInvalidWarningCount,
                this.OldDuplicateWarningCount,
                this.NewDuplicateWarningCount,
                this.IsNoOp ? "yes" : "no");
        }

        private static string[] NormalizeSeriesIds(IEnumerable<string>? seriesIds)
        {
            if (seriesIds == null)
            {
                return Array.Empty<string>();
            }

            return seriesIds
                .Where(seriesId => !string.IsNullOrWhiteSpace(seriesId))
                .Select(seriesId => seriesId.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(seriesId => seriesId, StringComparer.Ordinal)
                .ToArray();
        }
    }
}
