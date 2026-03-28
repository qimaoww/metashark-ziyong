// <copyright file="TvMissingImageRefillService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using Jellyfin.Data.Enums;
    using Jellyfin.Plugin.MetaShark.Providers;
    using MediaBrowser.Controller.BaseItemManager;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Configuration;
    using MediaBrowser.Model.IO;
    using Microsoft.Extensions.Logging;

    public sealed class TvMissingImageRefillService : ITvMissingImageRefillService
    {
        private static readonly Action<ILogger, int, Exception?> LogScanCandidates =
            LoggerMessage.Define<int>(LogLevel.Information, new EventId(1, nameof(QueueMissingImagesForFullLibraryScan)), "Found {Count} TV items for missing image refill scan.");

        private static readonly Action<ILogger, string, Guid, string, Exception?> LogQueuedRefresh =
            LoggerMessage.Define<string, Guid, string>(LogLevel.Debug, new EventId(2, nameof(QueueMissingImagesForFullLibraryScan)), "Queueing TV missing-image refill for {Name} ({Id}) with missing images {MissingImages}.");

        private static readonly Action<ILogger, Guid, Exception?> LogSkipEmptyId =
            LoggerMessage.Define<Guid>(LogLevel.Debug, new EventId(3, nameof(QueueMissingImagesForUpdatedItem)), "Skipping TV missing-image refill for empty Id ({Id}).");

        private static readonly Action<ILogger, string, Exception?> LogSkipDisabledImageFetcher =
            LoggerMessage.Define<string>(LogLevel.Debug, new EventId(4, nameof(QueueMissingImagesForUpdatedItem)), "Skipping TV missing-image refill because image fetcher is disabled for {Name}.");

        private static readonly Action<ILogger, string, Guid, Exception?> LogSkipNoMissingImages =
            LoggerMessage.Define<string, Guid>(LogLevel.Debug, new EventId(5, nameof(QueueIfMissingAndEnabled)), "Skipping TV missing-image refill for {Name} ({Id}) because no supported images are missing.");

        private static readonly Action<ILogger, string, Guid, ItemUpdateType, Exception?> LogSkipImageUpdate =
            LoggerMessage.Define<string, Guid, ItemUpdateType>(LogLevel.Debug, new EventId(6, nameof(QueueMissingImagesForUpdatedItem)), "Skipping TV missing-image refill for {Name} ({Id}) because update reason is {UpdateReason}.");

        private readonly ILogger<TvMissingImageRefillService> logger;
        private readonly ILibraryManager libraryManager;
        private readonly IProviderManager providerManager;
        private readonly IBaseItemManager baseItemManager;
        private readonly IFileSystem fileSystem;

        public TvMissingImageRefillService(
            ILoggerFactory loggerFactory,
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IBaseItemManager baseItemManager,
            IFileSystem fileSystem)
        {
            ArgumentNullException.ThrowIfNull(loggerFactory);

            this.logger = loggerFactory.CreateLogger<TvMissingImageRefillService>();
            this.libraryManager = libraryManager;
            this.providerManager = providerManager;
            this.baseItemManager = baseItemManager;
            this.fileSystem = fileSystem;
        }

        public void QueueMissingImagesForFullLibraryScan(CancellationToken cancellationToken)
        {
            var items = this.GetTvItemsForRefill();
            LogScanCandidates(this.logger, items.Count, null);

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                this.QueueIfMissingAndEnabled(item);
            }
        }

        public void QueueMissingImagesForUpdatedItem(ItemChangeEventArgs e, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(e);
            cancellationToken.ThrowIfCancellationRequested();

            var item = e.Item;
            if (e.UpdateReason == ItemUpdateType.ImageUpdate)
            {
                LogSkipImageUpdate(this.logger, item?.Name ?? string.Empty, item?.Id ?? Guid.Empty, e.UpdateReason, null);
                return;
            }

            this.QueueIfMissingAndEnabled(item);
        }

        private List<BaseItem> GetTvItemsForRefill()
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode },
                IsVirtualItem = false,
                IsMissing = false,
                Recursive = true,
            };

            return this.libraryManager.GetItemList(query);
        }

        private void QueueIfMissingAndEnabled(BaseItem? item)
        {
            if (item is not Series && item is not Season && item is not Episode)
            {
                return;
            }

            if (item.Id == Guid.Empty)
            {
                LogSkipEmptyId(this.logger, item.Id, null);
                return;
            }

            var typeOptions = this.GetTypeOptions(item);
            if (!this.baseItemManager.IsImageFetcherEnabled(item, typeOptions, MetaSharkPlugin.PluginName))
            {
                LogSkipDisabledImageFetcher(this.logger, item.Name ?? item.Id.ToString(), null);
                return;
            }

            var missingImages = TvImageSupport.GetMissingImages(item).ToArray();
            if (missingImages.Length == 0)
            {
                LogSkipNoMissingImages(this.logger, item.Name ?? string.Empty, item.Id, null);
                return;
            }

            var refreshOptions = new MetadataRefreshOptions(new DirectoryService(this.fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = false,
                ReplaceAllImages = false,
            };

            LogQueuedRefresh(this.logger, item.Name ?? string.Empty, item.Id, string.Join(",", missingImages), null);
            this.providerManager.QueueRefresh(item.Id, refreshOptions, RefreshPriority.Normal);
        }

        private TypeOptions? GetTypeOptions(BaseItem item)
        {
            var libraryOptions = this.libraryManager.GetLibraryOptions(item);
            var typeOptions = libraryOptions?.TypeOptions;
            if (typeOptions == null)
            {
                return null;
            }

            var targetType = item switch
            {
                Series => nameof(Series),
                Season => nameof(Season),
                Episode => nameof(Episode),
                _ => string.Empty,
            };

            if (string.IsNullOrEmpty(targetType))
            {
                return null;
            }

            return typeOptions.FirstOrDefault(x => string.Equals(x.Type, targetType, StringComparison.OrdinalIgnoreCase));
        }
    }
}
