// <copyright file="LlmTmdbCorrectionMetadataItemUpdatedWorker.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using MediaBrowser.Controller.Library;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;

    public sealed class LlmTmdbCorrectionMetadataItemUpdatedWorker : IHostedService
    {
        private static readonly Action<ILogger, Exception?> LogWorkerStart =
            LoggerMessage.Define(LogLevel.Information, new EventId(1, nameof(StartAsync)), "[MetaShark] 开始 LLM TMDb 纠错元数据条目更新工作器.");

        private static readonly Action<ILogger, string, Guid, string, Exception?> LogItemUpdated =
            LoggerMessage.Define<string, Guid, string>(LogLevel.Debug, new EventId(2, nameof(OnItemUpdated)), "[MetaShark] 收到 LLM TMDb 纠错元数据条目更新事件. trigger={Trigger} itemId={Id} itemPath={ItemPath}.");

        private static readonly Action<ILogger, Guid, string, Exception?> LogPostProcessFailed =
            LoggerMessage.Define<Guid, string>(LogLevel.Error, new EventId(3, nameof(OnItemUpdated)), "[MetaShark] LLM TMDb 纠错元数据后处理失败. itemId={Id} itemPath={ItemPath}.");

        private readonly ILibraryManager libraryManager;
        private readonly ILlmTmdbCorrectionMetadataPostProcessService postProcessService;
        private readonly ILogger<LlmTmdbCorrectionMetadataItemUpdatedWorker> logger;

        public LlmTmdbCorrectionMetadataItemUpdatedWorker(
            ILibraryManager libraryManager,
            ILlmTmdbCorrectionMetadataPostProcessService postProcessService,
            ILogger<LlmTmdbCorrectionMetadataItemUpdatedWorker> logger)
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

        internal void DispatchItemUpdated(ItemChangeEventArgs e)
        {
            this.postProcessService.TryApplyAsync(e, ILlmTmdbCorrectionMetadataPostProcessService.ItemUpdatedTrigger, CancellationToken.None).GetAwaiter().GetResult();
        }

        private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
        {
            var item = e.Item;
            var itemPath = item?.Path ?? string.Empty;
            LogItemUpdated(this.logger, ILlmTmdbCorrectionMetadataPostProcessService.ItemUpdatedTrigger, item?.Id ?? Guid.Empty, itemPath, null);

            try
            {
                this.DispatchItemUpdated(e);
            }
            catch (Exception ex)
            {
                // 插件异常不应沿 Jellyfin 的 ItemUpdated 事件栈向上传播，避免中断宿主扫描/刷新流程。
                LogPostProcessFailed(this.logger, item?.Id ?? Guid.Empty, itemPath, ex);
            }
        }
    }
}
