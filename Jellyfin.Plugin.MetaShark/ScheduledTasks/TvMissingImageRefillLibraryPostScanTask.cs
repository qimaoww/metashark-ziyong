// <copyright file="TvMissingImageRefillLibraryPostScanTask.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.ScheduledTasks
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Workers;
    using MediaBrowser.Controller.Library;
    using Microsoft.Extensions.Logging;

    public sealed class TvMissingImageRefillLibraryPostScanTask : ILibraryPostScanTask
    {
        private static readonly Action<ILogger, Exception?> LogTaskStart =
            LoggerMessage.Define(LogLevel.Information, new EventId(1, nameof(Run)), "Starting TV missing-image refill library post-scan task.");

        private readonly ILogger<TvMissingImageRefillLibraryPostScanTask> logger;
        private readonly ITvMissingImageRefillService refillService;

        public TvMissingImageRefillLibraryPostScanTask(
            ILogger<TvMissingImageRefillLibraryPostScanTask> logger,
            ITvMissingImageRefillService refillService)
        {
            this.logger = logger;
            this.refillService = refillService;
        }

        public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(progress);
            LogTaskStart(this.logger, null);
            this.refillService.QueueMissingImagesForFullLibraryScan(cancellationToken);
            progress.Report(100);
            return Task.CompletedTask;
        }
    }
}
