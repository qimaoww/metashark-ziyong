// <copyright file="PersonMissingImageRefillItemUpdatedWorker.cs" company="PlaceholderCompany">
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

    public sealed class PersonMissingImageRefillItemUpdatedWorker : IHostedService
    {
        private static readonly Action<ILogger, Exception?> LogWorkerStart =
            LoggerMessage.Define(LogLevel.Information, new EventId(1, nameof(StartAsync)), "[MetaShark] 开始人物缺图回填条目更新工作器.");

        private static readonly Action<ILogger, string, Guid, ItemUpdateType, Exception?> LogItemUpdated =
            LoggerMessage.Define<string, Guid, ItemUpdateType>(LogLevel.Debug, new EventId(2, nameof(OnItemUpdated)), "[MetaShark] 收到人物缺图回填条目更新事件. name={Name} itemId={Id} updateReason={UpdateReason}.");

        private static readonly Action<ILogger, Guid, ItemUpdateType, Exception?> LogPostProcessFailed =
            LoggerMessage.Define<Guid, ItemUpdateType>(LogLevel.Error, new EventId(3, nameof(OnItemUpdated)), "[MetaShark] 人物缺图回填条目更新处理失败. itemId={Id} updateReason={UpdateReason}.");

        private readonly ILibraryManager libraryManager;
        private readonly IPersonMissingImageRefillService refillService;
        private readonly ILogger<PersonMissingImageRefillItemUpdatedWorker> logger;

        public PersonMissingImageRefillItemUpdatedWorker(
            ILibraryManager libraryManager,
            IPersonMissingImageRefillService refillService,
            ILogger<PersonMissingImageRefillItemUpdatedWorker> logger)
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

        internal void DispatchItemUpdated(ItemChangeEventArgs e)
        {
            this.refillService.QueueMissingImagesForUpdatedItem(e, CancellationToken.None);
        }

        private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
        {
            var item = e.Item;
            LogItemUpdated(this.logger, item?.Name ?? string.Empty, item?.Id ?? Guid.Empty, e.UpdateReason, null);

            try
            {
                this.DispatchItemUpdated(e);
            }
            catch (Exception ex)
            {
                LogPostProcessFailed(this.logger, item?.Id ?? Guid.Empty, e.UpdateReason, ex);
            }
        }
    }
}
