// <copyright file="LlmEpisodeGroupMappingAssistRequest.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    using System;
    using System.Collections.Generic;
    using Jellyfin.Plugin.MetaShark.Configuration;

    public sealed class LlmEpisodeGroupMappingAssistRequest
    {
        public PluginConfiguration? Configuration { get; set; }

        public int? SeriesTmdbId { get; set; }

        public string? SeriesTitle { get; set; }

        public IEnumerable<string?> SafeRelativePathSamples { get; set; } = Array.Empty<string?>();

        public IEnumerable<LlmEpisodeDistributionItem?> EpisodeDistribution { get; set; } = Array.Empty<LlmEpisodeDistributionItem?>();

        public IEnumerable<LlmEpisodeGroupCandidate?> CandidateGroups { get; set; } = Array.Empty<LlmEpisodeGroupCandidate?>();

        public string? MetadataLanguage { get; set; }

        public bool IsManualTrigger { get; set; }
    }
}
