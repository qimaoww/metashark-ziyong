// <copyright file="LlmPromptContext.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    public sealed class LlmPromptContext
    {
        public string? MediaType { get; set; }

        public string? Name { get; set; }

        public string? RelativePath { get; set; }

        public string? FileName { get; set; }

        public string? ParentFolderName { get; set; }

        public string? MetadataLanguage { get; set; }

        public int? Year { get; set; }

        public int? SeasonNumber { get; set; }

        public int? EpisodeNumber { get; set; }

        public string? SeriesDisplayOrder { get; set; }

        public string? ParsedName { get; set; }

        public string? ParsedChineseName { get; set; }

        public int? ParsedYear { get; set; }

        public int? ParsedSeasonNumber { get; set; }

        public int? ParsedEpisodeNumber { get; set; }

        public bool ParsedIsSpecial { get; set; }

        public bool ParsedIsExtra { get; set; }

        public LlmProviderIdsSummary ExistingProviderIdsSummary { get; set; } = new LlmProviderIdsSummary();
    }
}
