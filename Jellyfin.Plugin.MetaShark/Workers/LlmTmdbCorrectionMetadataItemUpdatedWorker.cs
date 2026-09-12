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

    public sealed class LlmTmdbCorrectionMetadataItemUpdatedWorker : IHostedService, IDisposable
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
        private readonly ItemUpdateDispatchQueue dispatchQueue;

        public LlmTmdbCorrectionMetadataItemUpdatedWorker(
            ILibraryManager libraryManager,
            ILlmTmdbCorrectionMetadataPostProcessService postProcessService,
            ILogger<LlmTmdbCorrectionMetadataItemUpdatedWorker> logger)
        {
            this.libraryManager = libraryManager;
            this.postProcessService = postProcessService;
            this.logger = logger;
            this.dispatchQueue = new ItemUpdateDispatchQueue(this.ProcessItemUpdatedAsync, logger);
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            LogWorkerStart(this.logger, null);
            this.libraryManager.ItemUpdated += this.OnItemUpdated;
            this.dispatchQueue.Start(cancellationToken);
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            this.libraryManager.ItemUpdated -= this.OnItemUpdated;
            await this.dispatchQueue.StopAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            this.dispatchQueue.Dispose();
            GC.SuppressFinalize(this);
        }

        internal void DispatchItemUpdated(ItemChangeEventArgs e)
        {
            this.postProcessService.TryApplyAsync(e, ILlmTmdbCorrectionMetadataPostProcessService.ItemUpdatedTrigger, CancellationToken.None).GetAwaiter().GetResult();
        }

        internal Task WaitForPendingUpdatesAsync()
        {
            return this.dispatchQueue.WaitForIdleAsync();
        }

        private async Task ProcessItemUpdatedAsync(ItemChangeEventArgs e, CancellationToken cancellationToken)
        {
            var item = e.Item;
            var itemPath = item?.Path ?? string.Empty;
            try
            {
                await this.postProcessService.TryApplyAsync(e, ILlmTmdbCorrectionMetadataPostProcessService.ItemUpdatedTrigger, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031
            catch (Exception ex)
            {
                LogPostProcessFailed(this.logger, item?.Id ?? Guid.Empty, itemPath, ex);
            }
#pragma warning restore CA1031
        }

        private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
        {
            var item = e.Item;
            var itemPath = item?.Path ?? string.Empty;
            LogItemUpdated(this.logger, ILlmTmdbCorrectionMetadataPostProcessService.ItemUpdatedTrigger, item?.Id ?? Guid.Empty, itemPath, null);
            this.dispatchQueue.TryEnqueue(e);
        }
    }
}
