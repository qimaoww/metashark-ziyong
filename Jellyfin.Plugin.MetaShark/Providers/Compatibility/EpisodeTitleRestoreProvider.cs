// <copyright file="EpisodeTitleRestoreProvider.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Compatibility
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;

    /// <summary>
    /// Restores an existing title after ProbeProvider, still before local and remote metadata providers.
    /// </summary>
    public sealed class EpisodeTitleRestoreProvider : ICustomMetadataProvider<Episode>, IPreRefreshProvider, IHasOrder
    {
        private readonly EpisodeRefreshTitleGuard guard;

        public EpisodeTitleRestoreProvider(EpisodeRefreshTitleGuard guard)
        {
            ArgumentNullException.ThrowIfNull(guard);
            this.guard = guard;
        }

        public string Name => "MetaShark Episode Title Restore";

        public int Order => 101;

        public Task<ItemUpdateType> FetchAsync(Episode item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(this.guard.Restore(item, options));
        }
    }
}
