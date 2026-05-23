// <copyright file="MetadataAuthorityContext.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers
{
    using Jellyfin.Plugin.MetaShark.Model;

    internal sealed record MetadataAuthorityContext(
        string? Sid,
        string? TmdbId,
        MetaSource MetaSource,
        string? EffectiveSid,
        bool TmdbSourceIsPrimary,
        bool HasTmdbMeta,
        bool HasDoubanMeta)
    {
        public static MetadataAuthorityContext Create(string? sid, string? tmdbId, MetaSource metaSource, DefaultScraperSemantic semantic, bool doubanAllowed, bool hasPersistedDoubanTmdbCorrection)
        {
            if (metaSource == MetaSource.Tmdb && string.IsNullOrWhiteSpace(tmdbId))
            {
                metaSource = MetaSource.None;
            }

            if (hasPersistedDoubanTmdbCorrection)
            {
                metaSource = MetaSource.Tmdb;
            }

            var effectiveSid = doubanAllowed ? sid : null;
            var tmdbSourceIsPrimary = hasPersistedDoubanTmdbCorrection
                || (metaSource == MetaSource.Tmdb
                    && (!doubanAllowed
                        || (semantic != DefaultScraperSemantic.OverwriteRefresh && string.IsNullOrWhiteSpace(sid))));
            var hasTmdbMeta = !string.IsNullOrEmpty(tmdbId) && (!doubanAllowed || tmdbSourceIsPrimary);
            var hasDoubanMeta = !tmdbSourceIsPrimary && !string.IsNullOrEmpty(effectiveSid);

            return new MetadataAuthorityContext(
                sid,
                tmdbId,
                metaSource,
                effectiveSid,
                tmdbSourceIsPrimary,
                hasTmdbMeta,
                hasDoubanMeta);
        }
    }
}
