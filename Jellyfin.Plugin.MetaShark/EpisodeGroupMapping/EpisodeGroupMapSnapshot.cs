// <copyright file="EpisodeGroupMapSnapshot.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;

    public sealed class EpisodeGroupMapSnapshot
    {
        private static readonly IReadOnlyDictionary<string, string> EmptyMappings =
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        private static readonly IReadOnlyList<string> EmptyWarnings = Array.Empty<string>();
        private static readonly IReadOnlyCollection<string> EmptySeriesIds = Array.Empty<string>();

        public EpisodeGroupMapSnapshot(
            IReadOnlyDictionary<string, string>? groupIdsBySeriesId = null,
            IReadOnlyCollection<string>? mappedSeriesIds = null,
            IReadOnlyList<string>? invalidWarnings = null,
            IReadOnlyList<string>? duplicateWarnings = null,
            string? canonicalText = null)
        {
            this.GroupIdsBySeriesId = groupIdsBySeriesId ?? EmptyMappings;
            this.MappedSeriesIds = mappedSeriesIds ?? EmptySeriesIds;
            this.InvalidWarnings = invalidWarnings ?? EmptyWarnings;
            this.DuplicateWarnings = duplicateWarnings ?? EmptyWarnings;
            this.CanonicalText = canonicalText ?? string.Empty;
        }

        public static EpisodeGroupMapSnapshot Empty { get; } = new();

        public IReadOnlyDictionary<string, string> GroupIdsBySeriesId { get; }

        public IReadOnlyCollection<string> MappedSeriesIds { get; }

        public IReadOnlyList<string> InvalidWarnings { get; }

        public IReadOnlyList<string> DuplicateWarnings { get; }

        public string CanonicalText { get; }

        public bool TryGetGroupId(string? tmdbSeriesId, out string groupId)
        {
            if (!string.IsNullOrWhiteSpace(tmdbSeriesId)
                && this.GroupIdsBySeriesId.TryGetValue(tmdbSeriesId.Trim(), out var resolvedGroupId))
            {
                groupId = resolvedGroupId;
                return true;
            }

            groupId = string.Empty;
            return false;
        }
    }
}
