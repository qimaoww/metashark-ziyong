// <copyright file="LlmTmdbIdCorrectionRequest.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Collections.Generic;
    using Jellyfin.Plugin.MetaShark.Configuration;
    using MediaBrowser.Controller.Providers;
    using Microsoft.AspNetCore.Http;

    public sealed class LlmTmdbIdCorrectionRequest
    {
        public PluginConfiguration? Configuration { get; set; }

        public ItemLookupInfo? LookupInfo { get; set; }

        public string? MediaType { get; set; }

        public string? OldTmdbId { get; set; }

        public string? Name { get; set; }

        public int? Year { get; set; }

        public DefaultScraperSemantic Semantic { get; set; }

        public bool IsImageProvider { get; set; }

        public HttpContext? HttpContext { get; set; }

        public bool HasBridgedExplicitSearchMissingMetadataRefreshIntent { get; set; }

        public IEnumerable<string?> LibraryRoots { get; set; } = Array.Empty<string?>();

        public IEnumerable<string?> RelativePathSamples { get; set; } = Array.Empty<string?>();
    }
}
