// <copyright file="JellyfinEpisodeOverviewCleanupPersistence.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;

    public sealed class JellyfinEpisodeOverviewCleanupPersistence : IEpisodeOverviewCleanupPersistence
    {
        public async Task SaveAsync(Episode episode, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(episode);

            episode.OnMetadataChanged();
            await episode.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
        }
    }
}
