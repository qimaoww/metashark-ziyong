// <copyright file="TvMissingImageRefillItemUpdatedWorker.cs" company="PlaceholderCompany">
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

    public sealed class TvMissingImageRefillItemUpdatedWorker : IHostedService
    {
        private static readonly Action<ILogger, Exception?> LogWorkerStart =
            LoggerMessage.Define(LogLevel.Information, new EventId(1, nameof(StartAsync)), "Starting TV missing-image refill item-updated worker.");

        private static readonly Action<ILogger, string, Guid, ItemUpdateType, Exception?> LogItemUpdated =
            LoggerMessage.Define<string, Guid, ItemUpdateType>(LogLevel.Debug, new EventId(2, nameof(OnItemUpdated)), "Received TV item-updated event for {Name} ({Id}) with reason {UpdateReason}.");

        private readonly ILibraryManager libraryManager;
        private readonly ITvMissingImageRefillService refillService;
        private readonly ILogger<TvMissingImageRefillItemUpdatedWorker> logger;

        public TvMissingImageRefillItemUpdatedWorker(
            ILibraryManager libraryManager,
            ITvMissingImageRefillService refillService,
            ILogger<TvMissingImageRefillItemUpdatedWorker> logger)
        {
            this.libraryManager = libraryManager;
            this.refillService = refillService;
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
            this.refillService.QueueMissingImagesForUpdatedItem(e, CancellationToken.None);
        }
    }
}
