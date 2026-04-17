// <copyright file="PeriodicMissingMetadataSearchTask.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.ScheduledTasks
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Workers;
    using MediaBrowser.Model.Tasks;

    public sealed class PeriodicMissingMetadataSearchTask : IScheduledTask
    {
        private readonly IMissingMetadataSearchService missingMetadataSearchService;

        public PeriodicMissingMetadataSearchTask(IMissingMetadataSearchService missingMetadataSearchService)
        {
            ArgumentNullException.ThrowIfNull(missingMetadataSearchService);
            this.missingMetadataSearchService = missingMetadataSearchService;
        }

        public string Key => "MetaSharkPeriodicMissingMetadataSearch";

        public string Name => "定时搜索缺失元数据";

        public string Description => "按计划扫描全库并搜索缺失元数据";

        public string Category => MetaSharkPlugin.PluginName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(0).Ticks,
            };
        }

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(progress);
            return this.missingMetadataSearchService.RunFullLibrarySearchAsync(progress, cancellationToken);
        }
    }
}
