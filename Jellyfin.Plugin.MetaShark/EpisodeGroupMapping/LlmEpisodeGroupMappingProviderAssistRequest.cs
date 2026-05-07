// <copyright file="LlmEpisodeGroupMappingProviderAssistRequest.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    using System;
    using System.Collections.Generic;
    using Jellyfin.Plugin.MetaShark.Configuration;
    using Jellyfin.Plugin.MetaShark.Providers;
    using Microsoft.AspNetCore.Http;

    public sealed class LlmEpisodeGroupMappingProviderAssistRequest
    {
        public PluginConfiguration? Configuration { get; set; }

        public int? SeriesTmdbId { get; set; }

        public string? SeriesTitle { get; set; }

        public string? MetadataLanguage { get; set; }

        public string? MediaType { get; set; }

        public DefaultScraperSemantic Semantic { get; set; }

        public HttpContext? HttpContext { get; set; }

        public IEnumerable<string?> SafeRelativePathSamples { get; set; } = Array.Empty<string?>();

        public IEnumerable<LlmEpisodeDistributionItem?> EpisodeDistribution { get; set; } = Array.Empty<LlmEpisodeDistributionItem?>();
    }
}
