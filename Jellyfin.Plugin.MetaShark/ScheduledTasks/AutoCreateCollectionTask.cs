// <copyright file="AutoCreateCollectionTask.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.ScheduledTasks
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using MediaBrowser.Controller.Collections;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Model.Tasks;
    using Microsoft.Extensions.Logging;

    public sealed class AutoCreateCollectionTask : IScheduledTask, IDisposable
    {
        private static readonly Action<ILogger, Exception?> LogStart =
            LoggerMessage.Define(LogLevel.Information, new EventId(1, nameof(ExecuteAsync)), "[MetaShark] 开始自动创建合集扫描.");

        private static readonly Action<ILogger, Exception?> LogCompleted =
            LoggerMessage.Define(LogLevel.Information, new EventId(2, nameof(ExecuteAsync)), "[MetaShark] 自动创建合集扫描执行完成.");

        private readonly BoxSetManager boxSetManager;
        private readonly ILogger logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="AutoCreateCollectionTask"/> class.
        /// </summary>
        /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
        /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
        public AutoCreateCollectionTask(ILoggerFactory loggerFactory, ILibraryManager libraryManager, ICollectionManager collectionManager)
        {
            this.logger = loggerFactory.CreateLogger<AutoCreateCollectionTask>();
            this.boxSetManager = new BoxSetManager(libraryManager, collectionManager, loggerFactory);
        }

        public string Key => $"{MetaSharkPlugin.PluginName}AutoCreateCollection";

        public string Name => "扫描自动创建合集";

        public string Description => $"扫描媒体库创建合集，需要先在配置中开启获取电影系列信息";

        public string Category => MetaSharkPlugin.PluginName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(0).Ticks,
            };
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(progress);
            LogStart(this.logger, null);
            await this.boxSetManager.ScanLibrary(progress).ConfigureAwait(false);
            LogCompleted(this.logger, null);
        }

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.boxSetManager.Dispose();
            }
        }
    }
}
