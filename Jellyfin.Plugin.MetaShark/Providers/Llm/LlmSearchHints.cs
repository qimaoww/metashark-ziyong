// <copyright file="LlmSearchHints.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    public sealed class LlmSearchHints
    {
        public string? Title { get; set; }

        public int? Year { get; set; }

        public bool HasHints => !string.IsNullOrWhiteSpace(this.Title) || this.Year.HasValue;
    }
}
