// <copyright file="LlmTmdbCorrectionMapEntry.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    public sealed class LlmTmdbCorrectionMapEntry
    {
        public LlmTmdbCorrectionMapEntry(string mediaType, string sourceProvider, string sourceId, string tmdbId)
        {
            this.MediaType = mediaType;
            this.SourceProvider = sourceProvider;
            this.SourceId = sourceId;
            this.TmdbId = tmdbId;
        }

        public string MediaType { get; }

        public string SourceProvider { get; }

        public string SourceId { get; }

        public string TmdbId { get; }

        public string Key => $"{this.MediaType}:{this.SourceProvider}:{this.SourceId}";

        public string Value => $"tmdb:{this.TmdbId}";
    }
}
