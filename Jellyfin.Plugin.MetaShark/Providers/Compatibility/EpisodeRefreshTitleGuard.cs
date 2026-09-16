// <copyright file="EpisodeRefreshTitleGuard.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Compatibility
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using Jellyfin.Plugin.MetaShark.Core;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Preserves existing episode names across Jellyfin's embedded-title probe during a missing-metadata refresh.
    /// </summary>
    public sealed class EpisodeRefreshTitleGuard
    {
        private static readonly Action<ILogger, Guid, Exception?> LogTitlePreserved =
            LoggerMessage.Define<Guid>(LogLevel.Debug, new EventId(1, nameof(LogTitlePreserved)), "[MetaShark] 搜索缺失元数据时已保留探测前的单集标题，阻止内嵌标题覆盖. itemId={ItemId}");

        private readonly ConditionalWeakTable<MetadataRefreshOptions, ConcurrentDictionary<Episode, string>> snapshots = new();
        private readonly ILibraryManager libraryManager;
        private readonly MetaSharkOrdinaryItemLibraryCapabilityResolver capabilityResolver;
        private readonly ILogger<EpisodeRefreshTitleGuard> logger;

        public EpisodeRefreshTitleGuard(
            ILibraryManager libraryManager,
            MetaSharkOrdinaryItemLibraryCapabilityResolver capabilityResolver,
            ILogger<EpisodeRefreshTitleGuard> logger)
        {
            ArgumentNullException.ThrowIfNull(libraryManager);
            ArgumentNullException.ThrowIfNull(capabilityResolver);
            ArgumentNullException.ThrowIfNull(logger);
            this.libraryManager = libraryManager;
            this.capabilityResolver = capabilityResolver;
            this.logger = logger;
        }

        internal void Capture(Episode item, MetadataRefreshOptions options)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(options);

            var originalTitle = item.Name;
            if (!IsSearchMissingMetadata(options)
                || item.IsLocked
                || item.LockedFields.Contains(MetadataField.Name)
                || item.IsMissingEpisode
                || item.ExtraType.HasValue
                || string.IsNullOrWhiteSpace(originalTitle)
                || this.libraryManager.GetLibraryOptions(item)?.EnableEmbeddedTitles != true
                || !this.capabilityResolver.Resolve(item, MetaSharkLibraryCapability.Metadata).Allowed)
            {
                return;
            }

            // Options are shared by children of a recursive refresh: key by both refresh and
            // actual item instance. Weak ownership also releases snapshots if a refresh is cancelled.
            this.snapshots.GetValue(options, static _ => new ConcurrentDictionary<Episode, string>(ReferenceEqualityComparer.Instance))
                .TryAdd(item, originalTitle);
        }

        internal ItemUpdateType Restore(Episode item, MetadataRefreshOptions options)
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentNullException.ThrowIfNull(options);

            if (!this.snapshots.TryGetValue(options, out var items)
                || !items.TryRemove(item, out var originalTitle)
                || !IsSearchMissingMetadata(options)
                || item.IsLocked
                || item.LockedFields.Contains(MetadataField.Name)
                || string.Equals(item.Name, originalTitle, StringComparison.Ordinal))
            {
                return ItemUpdateType.None;
            }

            // Jellyfin 12 imports embedded Name unconditionally, then preserves it when
            // ReplaceAllMetadata=false. Restore BEFORE remote providers and the final merge,
            // leaving scraping, local metadata and default-title backfill rules unchanged.
            item.Name = originalTitle;
            LogTitlePreserved(this.logger, item.Id, null);
            return ItemUpdateType.MetadataImport;
        }

        private static bool IsSearchMissingMetadata(MetadataRefreshOptions options)
        {
            return options.MetadataRefreshMode == MetadataRefreshMode.FullRefresh
                && !options.ReplaceAllMetadata
                && !options.RemoveOldMetadata
                && options.SearchResult == null;
        }
    }
}
