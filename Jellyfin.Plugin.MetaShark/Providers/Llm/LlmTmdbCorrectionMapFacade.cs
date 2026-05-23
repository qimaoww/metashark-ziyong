// <copyright file="LlmTmdbCorrectionMapFacade.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Linq;
    using Jellyfin.Plugin.MetaShark.Configuration;

    public sealed class LlmTmdbCorrectionMapFacade : ILlmTmdbCorrectionMapFacade
    {
        private readonly LlmTmdbCorrectionMapParser parser;

        public LlmTmdbCorrectionMapFacade(LlmTmdbCorrectionMapParser? parser = null)
        {
            this.parser = parser ?? LlmTmdbCorrectionMapParser.Shared;
        }

        public bool TryGetCorrection(PluginConfiguration configuration, string mediaType, string? doubanId, out string tmdbId)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            tmdbId = string.Empty;
            return configuration.EnableLlmTmdbCorrectionPersistence
                && this.parser.TryGetDoubanCorrection(configuration.LlmTmdbCorrectionMap, mediaType, doubanId, out tmdbId)
                && !string.IsNullOrWhiteSpace(tmdbId);
        }

        public bool TryGetCompletion(PluginConfiguration configuration, string mediaType, string? doubanId, out string tmdbId)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            tmdbId = string.Empty;
            return configuration.EnableLlmTmdbCompletionPersistence
                && this.parser.TryGetDoubanCorrection(configuration.LlmTmdbCompletionMap, mediaType, doubanId, out tmdbId)
                && !string.IsNullOrWhiteSpace(tmdbId);
        }

        public bool TryFindCorrectionByTmdbId(PluginConfiguration configuration, string mediaType, string? tmdbId, out string correctedTmdbId)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            correctedTmdbId = string.Empty;
            if (!configuration.EnableLlmTmdbCorrectionPersistence || string.IsNullOrWhiteSpace(tmdbId))
            {
                return false;
            }

            var trimmedTmdbId = tmdbId.Trim();
            var matchedEntry = this.parser.ParseSnapshot(configuration.LlmTmdbCorrectionMap).EntriesByKey.Values.FirstOrDefault(entry =>
                string.Equals(entry.MediaType, mediaType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(entry.TmdbId, trimmedTmdbId, StringComparison.OrdinalIgnoreCase));
            if (matchedEntry == null)
            {
                return false;
            }

            correctedTmdbId = matchedEntry.TmdbId;
            return true;
        }
    }
}
