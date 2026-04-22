// <copyright file="PersonMissingImageRefillLibraryPostScanTask.cs" company="PlaceholderCompany">
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

    public sealed class PersonMissingImageRefillLibraryPostScanTask : ILibraryPostScanTask
    {
        private static readonly Action<ILogger, Exception?> LogTaskStart =
            LoggerMessage.Define(LogLevel.Information, new EventId(1, nameof(Run)), "[MetaShark] 开始人物缺图回填媒体库扫描后任务，准备排队缺图回填.");

        private static readonly Action<ILogger, int, int, int, bool, Exception?> LogTaskFinished =
            LoggerMessage.Define<int, int, int, bool>(LogLevel.Information, new EventId(2, nameof(Run)), "[MetaShark] 人物缺图回填媒体库扫描后任务已完成排队，后台补图异步继续. candidateCount={CandidateCount} queuedCount={QueuedCount} skippedCount={SkippedCount} refillContinuesAsync={RefillContinuesAsync}.");

        private readonly ILogger<PersonMissingImageRefillLibraryPostScanTask> logger;
        private readonly IPersonMissingImageRefillService refillService;

        public PersonMissingImageRefillLibraryPostScanTask(
            ILogger<PersonMissingImageRefillLibraryPostScanTask> logger,
            IPersonMissingImageRefillService refillService)
        {
            this.logger = logger;
            this.refillService = refillService;
        }

        public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(progress);
            LogTaskStart(this.logger, null);
            var summary = this.refillService.QueueMissingImagesForFullLibraryScan(cancellationToken);
            progress.Report(100);
            LogTaskFinished(this.logger, summary.CandidateCount, summary.QueuedCount, summary.SkippedCount, true, null);
            return Task.CompletedTask;
        }
    }
}
