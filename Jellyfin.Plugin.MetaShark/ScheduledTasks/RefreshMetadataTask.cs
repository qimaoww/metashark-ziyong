// <copyright file="RefreshMetadataTask.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.ScheduledTasks
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Data.Enums;
    using Jellyfin.Plugin.MetaShark.Core;
    using Jellyfin.Plugin.MetaShark.Providers;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.Movies;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.IO;
    using MediaBrowser.Model.Tasks;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Task to refresh metadata for items needing refresh.
    /// </summary>
    public class RefreshMetadataTask : IScheduledTask
    {
        private static readonly Action<ILogger, Exception?> LogTaskStart =
            LoggerMessage.Define(LogLevel.Information, new EventId(1, nameof(ExecuteAsync)), "[MetaShark] 开始刷新待重新刮削条目.");

        private static readonly Action<ILogger, Exception?> LogNoItems =
            LoggerMessage.Define(LogLevel.Information, new EventId(2, nameof(ExecuteAsync)), "[MetaShark] 未找到待重新刮削条目.");

        private static readonly Action<ILogger, int, Exception?> LogItemsFound =
            LoggerMessage.Define<int>(LogLevel.Information, new EventId(3, nameof(ExecuteAsync)), "[MetaShark] 找到 {Count} 个待重新刮削条目.");

        private static readonly Action<ILogger, string, Guid, Exception?> LogQueueRefresh =
            LoggerMessage.Define<string, Guid>(LogLevel.Debug, new EventId(4, nameof(ExecuteAsync)), "[MetaShark] 已排队刷新条目. name={Name} itemId={Id}.");

        private static readonly Action<ILogger, int, Exception?> LogFinished =
            LoggerMessage.Define<int>(LogLevel.Information, new EventId(5, nameof(ExecuteAsync)), "[MetaShark] 待重新刮削条目刷新排队完成. Count={Count}.");

        private readonly ILogger<RefreshMetadataTask> logger;
        private readonly ILibraryManager libraryManager;
        private readonly IProviderManager providerManager;

        private readonly IFileSystem fileSystem;

        /// <summary>
        /// Initializes a new instance of the <see cref="RefreshMetadataTask"/> class.
        /// </summary>
        public RefreshMetadataTask(
            ILogger<RefreshMetadataTask> logger,
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem)
        {
            this.logger = logger;
            this.libraryManager = libraryManager;
            this.providerManager = providerManager;
            this.fileSystem = fileSystem;
        }

        /// <inheritdoc />
        public string Name => "重新刮削失败的影片";

        /// <inheritdoc />
        public string Key => $"{MetaSharkPlugin.PluginName}RefreshMissingMetadata";

        /// <inheritdoc />
        public string Description => "重新刮削之前刮削失败的影片.";

        /// <inheritdoc />
        public string Category => MetaSharkPlugin.PluginName;

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // No default triggers, meant to be run manually.
            return Enumerable.Empty<TaskTriggerInfo>();
        }

        /// <inheritdoc />
        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(progress);
            LogTaskStart(this.logger, null);

            var itemsToRefresh = this.GetItemsNeedingMetadataRefresh();
            int totalItems = itemsToRefresh.Count;
            int processedCount = 0;

            if (totalItems == 0)
            {
                LogNoItems(this.logger, null);
                progress.Report(100);
                return;
            }

            LogItemsFound(this.logger, totalItems, null);

            foreach (var item in itemsToRefresh)
            {
                cancellationToken.ThrowIfCancellationRequested();

                LogQueueRefresh(this.logger, item.Name, item.Id, null);

                var refreshOptions = new MetadataRefreshOptions(new DirectoryService(this.fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                    ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                    ReplaceAllMetadata = false,
                    ReplaceAllImages = false,
                };

                this.providerManager.QueueRefresh(item.Id, refreshOptions, RefreshPriority.Normal);

                processedCount++;
                progress.Report(processedCount * 100.0 / totalItems);

                // 等待5秒，避免短时间内请求过多
                await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
            }

            LogFinished(this.logger, totalItems, null);
        }

        private List<BaseItem> GetItemsNeedingMetadataRefresh()
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                IsVirtualItem = false,
                IsMissing = false,
                Recursive = true,
            };

            var items = this.libraryManager.GetItemList(query);

            return items.Where(item =>
            (!item.ProviderIds.ContainsKey(BaseProvider.DoubanProviderId) && !item.HasImage(ImageType.Primary)) ||
             (File.Exists(item.Path) && !item.HasImage(ImageType.Primary)))
            .ToList();
        }
    }
}
