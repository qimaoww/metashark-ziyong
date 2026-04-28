// <copyright file="SeriesTmdbProviderIdMigrationWorker.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Data.Enums;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;

    public sealed class SeriesTmdbProviderIdMigrationWorker : IHostedService
    {
        private const string StartupTrigger = "StartupScan";
        private const string ItemUpdatedTrigger = "ItemUpdated";

        private static readonly Action<ILogger, Exception?> LogWorkerStart =
            LoggerMessage.Define(LogLevel.Information, new EventId(1, nameof(StartAsync)), "[MetaShark] 开始剧集官方 TMDb provider id 迁移工作器.");

        private static readonly Action<ILogger, string, Guid, ItemUpdateType, Exception?> LogItemUpdated =
            LoggerMessage.Define<string, Guid, ItemUpdateType>(LogLevel.Debug, new EventId(2, nameof(OnItemUpdated)), "[MetaShark] 收到剧集官方 TMDb provider id 迁移条目更新事件. name={Name} itemId={Id} updateReason={UpdateReason}.");

        private static readonly Action<ILogger, string, Exception?> LogStartupMigrationFailed =
            LoggerMessage.Define<string>(LogLevel.Error, new EventId(3, nameof(StartAsync)), "[MetaShark] 剧集官方 TMDb provider id 启动迁移失败. trigger={Trigger}.");

        private static readonly Action<ILogger, string, Guid, ItemUpdateType, Exception?> LogItemMigrationFailed =
            LoggerMessage.Define<string, Guid, ItemUpdateType>(LogLevel.Error, new EventId(4, nameof(OnItemUpdated)), "[MetaShark] 剧集官方 TMDb provider id 条目迁移失败. name={Name} itemId={Id} updateReason={UpdateReason}.");

        private readonly ILibraryManager libraryManager;
        private readonly SeriesTmdbProviderIdMigrationService migrationService;
        private readonly ILogger<SeriesTmdbProviderIdMigrationWorker> logger;
        private CancellationTokenSource? startupMigrationCancellationTokenSource;
        private Task? startupMigrationTask;

        public SeriesTmdbProviderIdMigrationWorker(
            ILibraryManager libraryManager,
            SeriesTmdbProviderIdMigrationService migrationService,
            ILogger<SeriesTmdbProviderIdMigrationWorker> logger)
        {
            this.libraryManager = libraryManager;
            this.migrationService = migrationService;
            this.logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            LogWorkerStart(this.logger, null);
            this.libraryManager.ItemUpdated += this.OnItemUpdated;
            this.startupMigrationCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            this.startupMigrationTask = Task.Run(
                () => this.RunStartupMigrationAsync(this.startupMigrationCancellationTokenSource.Token),
                CancellationToken.None);
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            this.libraryManager.ItemUpdated -= this.OnItemUpdated;
            if (this.startupMigrationCancellationTokenSource != null)
            {
                await this.startupMigrationCancellationTokenSource.CancelAsync().ConfigureAwait(false);
            }

            var task = this.startupMigrationTask;
            if (task != null)
            {
                try
                {
                    await task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
            }

            this.startupMigrationCancellationTokenSource?.Dispose();
            this.startupMigrationCancellationTokenSource = null;
            this.startupMigrationTask = null;
        }

        private async Task RunStartupMigrationAsync(CancellationToken cancellationToken)
        {
#pragma warning disable CA1031
            try
            {
                await this.migrationService.MigrateFullLibraryAsync(StartupTrigger, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                LogStartupMigrationFailed(this.logger, StartupTrigger, ex);
            }
#pragma warning restore CA1031
        }

        private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
        {
            var item = e.Item;
            if (item is not Series)
            {
                return;
            }

            LogItemUpdated(this.logger, item?.Name ?? string.Empty, item?.Id ?? Guid.Empty, e.UpdateReason, null);

#pragma warning disable CA1031
            try
            {
                this.migrationService.MigrateItemAsync(item, ItemUpdatedTrigger, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                LogItemMigrationFailed(this.logger, item?.Name ?? string.Empty, item?.Id ?? Guid.Empty, e.UpdateReason, ex);
            }
#pragma warning restore CA1031
        }
    }
}
