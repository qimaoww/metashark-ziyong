// <copyright file="LlmTmdbCorrectionMapSnapshot.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System.Collections.Generic;
    using System.Collections.ObjectModel;

    public sealed class LlmTmdbCorrectionMapSnapshot
    {
        public LlmTmdbCorrectionMapSnapshot(IReadOnlyDictionary<string, LlmTmdbCorrectionMapEntry> entriesByKey, string canonicalText)
        {
            this.EntriesByKey = new ReadOnlyDictionary<string, LlmTmdbCorrectionMapEntry>(new Dictionary<string, LlmTmdbCorrectionMapEntry>(entriesByKey));
            this.CanonicalText = canonicalText ?? string.Empty;
        }

        public IReadOnlyDictionary<string, LlmTmdbCorrectionMapEntry> EntriesByKey { get; }

        public string CanonicalText { get; }

        public bool TryGetDoubanCorrection(string mediaType, string? doubanId, out string tmdbId)
        {
            tmdbId = string.Empty;
            if (!LlmTmdbCorrectionMapParser.TryBuildDoubanKey(mediaType, doubanId, out var key))
            {
                return false;
            }

            if (!this.EntriesByKey.TryGetValue(key, out var entry))
            {
                return false;
            }

            tmdbId = entry.TmdbId;
            return true;
        }
    }
}
