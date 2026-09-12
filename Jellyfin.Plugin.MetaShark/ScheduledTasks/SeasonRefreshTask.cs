// <copyright file="SeasonRefreshTask.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.ScheduledTasks
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Data.Enums;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.IO;
    using MediaBrowser.Model.Tasks;
    using Microsoft.Extensions.Logging;

    public sealed class SeasonRefreshTask : IScheduledTask
    {
        private static readonly Action<ILogger, Exception?> LogStart =
            LoggerMessage.Define(LogLevel.Information, new EventId(1, nameof(ExecuteAsync)), "[MetaShark] 开始季刷新.");

        private static readonly Action<ILogger, Exception?> LogNoSeasons =
            LoggerMessage.Define(LogLevel.Information, new EventId(2, nameof(ExecuteAsync)), "[MetaShark] 未找到可执行季刷新的条目.");

        private static readonly Action<ILogger, string, Exception?> LogSkipEmptyId =
            LoggerMessage.Define<string>(LogLevel.Warning, new EventId(3, nameof(ExecuteAsync)), "[MetaShark] 跳过季刷新，条目 ID 为空. name={Name}.");

        private static readonly Action<ILogger, int, int, Exception?> LogFinished =
            LoggerMessage.Define<int, int>(LogLevel.Information, new EventId(4, nameof(ExecuteAsync)), "[MetaShark] 季刷新排队完成. Queued={Queued} Seasons={Seasons}.");

        private static readonly Action<ILogger, Exception?> LogScanRunning =
            LoggerMessage.Define(LogLevel.Information, new EventId(5, nameof(ExecuteAsync)), "[MetaShark] 媒体库扫描进行中，跳过季刷新. reason=LibraryScanRunning.");

        private readonly ILogger<SeasonRefreshTask> logger;
        private readonly ILibraryManager libraryManager;
        private readonly IProviderManager providerManager;
        private readonly IFileSystem fileSystem;

        public SeasonRefreshTask(
            ILogger<SeasonRefreshTask> logger,
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem)
        {
            this.logger = logger;
            this.libraryManager = libraryManager;
            this.providerManager = providerManager;
            this.fileSystem = fileSystem;
        }

        public string Key => $"{MetaSharkPlugin.PluginName}SeasonRefresh";

        public string Name => "季刷新";

        public string Description => "对全库所有季执行扫描新的和有修改的文件";

        public string Category => MetaSharkPlugin.PluginName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Enumerable.Empty<TaskTriggerInfo>();
        }

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(progress);

            // 与 Jellyfin 自带任务一致：扫描期间跳过，避免和扫描争用数据库与刷新队列。
            if (this.libraryManager.IsScanRunning)
            {
                LogScanRunning(this.logger, null);
                progress.Report(100);
                return Task.CompletedTask;
            }

            LogStart(this.logger, null);

            var seasons = this.libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Season },
                Recursive = true,

                // 后续只用 Id/Name 与刷新选项，跳过整库 Data JSON 反序列化。
                SkipDeserialization = true,
            });
            if (seasons.Count == 0)
            {
                LogNoSeasons(this.logger, null);
                progress.Report(100);
                return Task.CompletedTask;
            }

            var queued = 0;
            var processed = 0;
            foreach (var season in seasons)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (season.Id == Guid.Empty)
                {
                    LogSkipEmptyId(this.logger, season.Name ?? string.Empty, null);
                }
                else
                {
                    this.providerManager.QueueRefresh(season.Id, CreateRefreshOptions(this.fileSystem), RefreshPriority.High);
                    queued++;
                }

                processed++;
                progress.Report(processed * 100.0 / seasons.Count);
            }

            LogFinished(this.logger, queued, seasons.Count, null);
            return Task.CompletedTask;
        }

        private static MetadataRefreshOptions CreateRefreshOptions(IFileSystem fileSystem)
        {
            return new MetadataRefreshOptions(new DirectoryService(fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.Default,
                ImageRefreshMode = MetadataRefreshMode.Default,
                ReplaceAllMetadata = false,
                ReplaceAllImages = false,
                IsAutomated = false,
            };
        }
    }
}
