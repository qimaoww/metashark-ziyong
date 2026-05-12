// <copyright file="ITmdbEpisodeGroupMapPersistenceService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    using System.Threading;
    using System.Threading.Tasks;

    public interface ITmdbEpisodeGroupMapPersistenceService
    {
        Task<TmdbEpisodeGroupMapPersistenceResult> TrySaveAsync(string? expectedOldMapping, string? newMapping, CancellationToken cancellationToken);
    }
}
