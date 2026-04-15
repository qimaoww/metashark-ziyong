// <copyright file="EpisodeTitleBackfillItemUpdatedWorker.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Data.Enums;
    using MediaBrowser.Controller.Library;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;

    public sealed class EpisodeTitleBackfillItemUpdatedWorker : IHostedService
    {
        private static readonly Action<ILogger, Exception?> LogWorkerStart =
            LoggerMessage.Define(LogLevel.Information, new EventId(1, nameof(StartAsync)), "Starting episode-title-backfill item-updated worker.");

        private static readonly Action<ILogger, string, Guid, string, ItemUpdateType, Exception?> LogItemUpdated =
            LoggerMessage.Define<string, Guid, string, ItemUpdateType>(LogLevel.Debug, new EventId(2, nameof(OnItemUpdated)), "Received episode item-updated event for {Name} ({Id}) path {ItemPath} trigger=ItemUpdated updateReason={UpdateReason}.");

        private static readonly Action<ILogger, Guid, string, ItemUpdateType, Exception?> LogPostProcessFailed =
            LoggerMessage.Define<Guid, string, ItemUpdateType>(LogLevel.Error, new EventId(3, nameof(OnItemUpdated)), "Episode title backfill post-process failed for item {Id} path {ItemPath} trigger=ItemUpdated updateReason={UpdateReason}.");

        private readonly ILibraryManager libraryManager;
        private readonly IEpisodeTitleBackfillPostProcessService postProcessService;
        private readonly ILogger<EpisodeTitleBackfillItemUpdatedWorker> logger;

        public EpisodeTitleBackfillItemUpdatedWorker(
            ILibraryManager libraryManager,
            IEpisodeTitleBackfillPostProcessService postProcessService,
            ILogger<EpisodeTitleBackfillItemUpdatedWorker> logger)
        {
            this.libraryManager = libraryManager;
            this.postProcessService = postProcessService;
            this.logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            LogWorkerStart(this.logger, null);
            this.libraryManager.ItemUpdated += this.OnItemUpdated;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            this.libraryManager.ItemUpdated -= this.OnItemUpdated;
            return Task.CompletedTask;
        }

        private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
        {
            var item = e.Item;
            var itemPath = item?.Path ?? string.Empty;
            LogItemUpdated(this.logger, item?.Name ?? string.Empty, item?.Id ?? Guid.Empty, itemPath, e.UpdateReason, null);

            try
            {
                this.postProcessService.TryApplyAsync(e, IEpisodeTitleBackfillPostProcessService.ItemUpdatedTrigger, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                LogPostProcessFailed(this.logger, item?.Id ?? Guid.Empty, itemPath, e.UpdateReason, ex);
                throw;
            }
        }
    }
}
