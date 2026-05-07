// <copyright file="LlmScrapingSuggestion.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    public sealed class LlmScrapingSuggestion
    {
        public string? MediaType { get; set; }

        public string? Title { get; set; }

        public int? Year { get; set; }

        public int? SeasonNumber { get; set; }

        public int? EpisodeNumber { get; set; }

        public string? OriginalTitle { get; set; }

        public string? Overview { get; set; }

        public double Confidence { get; set; }
    }
}
