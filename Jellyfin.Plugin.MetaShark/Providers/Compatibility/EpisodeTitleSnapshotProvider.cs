// <copyright file="EpisodeTitleSnapshotProvider.cs" company="PlaceholderCompany">
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
    /// Captures the existing title immediately before Jellyfin's ProbeProvider (order 100).
    /// </summary>
    public sealed class EpisodeTitleSnapshotProvider : ICustomMetadataProvider<Episode>, IPreRefreshProvider, IHasOrder
    {
        private readonly EpisodeRefreshTitleGuard guard;

        public EpisodeTitleSnapshotProvider(EpisodeRefreshTitleGuard guard)
        {
            ArgumentNullException.ThrowIfNull(guard);
            this.guard = guard;
        }

        public string Name => "MetaShark Episode Title Snapshot";

        public int Order => 99;

        public Task<ItemUpdateType> FetchAsync(Episode item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.guard.Capture(item, options);
            return Task.FromResult(ItemUpdateType.None);
        }
    }
}
