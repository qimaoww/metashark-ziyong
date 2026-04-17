// <copyright file="TmdbEpisodeGroupMapping.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Core
{
    using Jellyfin.Plugin.MetaShark.EpisodeGroupMapping;

    public static class TmdbEpisodeGroupMapping
    {
        public static bool TryGetGroupId(string? mapping, string? tmdbSeriesId, out string groupId)
        {
            return EpisodeGroupMapParser.Shared.TryGetGroupId(mapping, tmdbSeriesId, out groupId);
        }
    }
}
