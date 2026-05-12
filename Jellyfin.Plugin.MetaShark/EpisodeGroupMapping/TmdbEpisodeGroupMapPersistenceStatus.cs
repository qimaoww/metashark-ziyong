// <copyright file="TmdbEpisodeGroupMapPersistenceStatus.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    public enum TmdbEpisodeGroupMapPersistenceStatus
    {
        Failed,
        NoChange,
        Conflict,
        Saved,
    }
}
