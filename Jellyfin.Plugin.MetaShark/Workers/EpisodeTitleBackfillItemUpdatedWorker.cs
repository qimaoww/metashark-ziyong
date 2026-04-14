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

        private static readonly Action<ILogger, string, Guid, ItemUpdateType, Exception?> LogItemUpdated =
            LoggerMessage.Define<string, Guid, ItemUpdateType>(LogLevel.Debug, new EventId(2, nameof(OnItemUpdated)), "Received episode item-updated event for {Name} ({Id}) with reason {UpdateReason}.");

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
            LogItemUpdated(this.logger, item?.Name ?? string.Empty, item?.Id ?? Guid.Empty, e.UpdateReason, null);
            this.postProcessService.ProcessUpdatedItemAsync(e, CancellationToken.None).GetAwaiter().GetResult();
        }
    }
}
