// <copyright file="LlmExternalIdPromptContext.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Collections.Generic;

    public sealed class LlmExternalIdPromptContext
    {
        public string Task { get; set; } = string.Empty;

        public string OutputSchema { get; set; } = string.Empty;

        public IReadOnlyList<string> Constraints { get; set; } = Array.Empty<string>();

        public string? MediaType { get; set; }

        public string? Title { get; set; }

        public int? Year { get; set; }

        public int? SeasonNumber { get; set; }

        public int? EpisodeNumber { get; set; }

        public IReadOnlyList<string> SafeRelativePathSamples { get; set; } = Array.Empty<string>();

        public IReadOnlyDictionary<string, string> PublicProviderIds { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
