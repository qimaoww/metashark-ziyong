// <copyright file="ItemUpdateDispatchQueue.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Threading;
    using System.Threading.Channels;
    using System.Threading.Tasks;
    using MediaBrowser.Controller.Library;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// Jellyfin ItemUpdated 事件的单消费者后台队列：把插件后处理从宿主事件线程上搬开，
    /// 避免同步等待文件/数据库 IO，同时保证插件异常不会抛回宿主调用栈。
    /// </summary>
    internal sealed class ItemUpdateDispatchQueue : IDisposable
    {
        private const int MaxPendingItems = 4096;

        private static readonly Action<ILogger, Guid, Exception?> LogHandlerFailed =
            LoggerMessage.Define<Guid>(LogLevel.Error, new EventId(1, nameof(PumpAsync)), "[MetaShark] 条目更新队列处理失败. itemId={ItemId}.");

        private static readonly Action<ILogger, Guid, Exception?> LogQueueFull =
            LoggerMessage.Define<Guid>(LogLevel.Warning, new EventId(2, nameof(TryEnqueue)), "[MetaShark] 条目更新队列已满，事件被丢弃. itemId={ItemId}.");

        private readonly Channel<ItemChangeEventArgs> channel;
        private readonly Func<ItemChangeEventArgs, CancellationToken, Task> handler;
        private readonly ILogger logger;
        private readonly object syncRoot = new object();

        private CancellationTokenSource? cancellation;
        private Task? pumpTask;
        private int pendingCount;
        private TaskCompletionSource? idleSource;

        public ItemUpdateDispatchQueue(Func<ItemChangeEventArgs, CancellationToken, Task> handler, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(handler);
            ArgumentNullException.ThrowIfNull(logger);

            this.handler = handler;
            this.logger = logger;
            this.channel = Channel.CreateBounded<ItemChangeEventArgs>(new BoundedChannelOptions(MaxPendingItems)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            });
        }

        public bool TryEnqueue(ItemChangeEventArgs e)
        {
            ArgumentNullException.ThrowIfNull(e);

            if (!this.channel.Writer.TryWrite(e))
            {
                LogQueueFull(this.logger, e.Item?.Id ?? Guid.Empty, null);
                return false;
            }

            lock (this.syncRoot)
            {
                this.pendingCount++;
            }

            return true;
        }

        public void Start(CancellationToken cancellationToken)
        {
            lock (this.syncRoot)
            {
                if (this.pumpTask != null)
                {
                    return;
                }

                this.cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                this.pumpTask = Task.Run(() => this.PumpAsync(this.cancellation.Token), CancellationToken.None);
            }
        }

        public async Task StopAsync()
        {
            CancellationTokenSource? cancellation;
            Task? pump;
            lock (this.syncRoot)
            {
                cancellation = this.cancellation;
                pump = this.pumpTask;
                this.cancellation = null;
                this.pumpTask = null;
            }

            cancellation?.Cancel();

            if (pump != null)
            {
                try
                {
                    await pump.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            cancellation?.Dispose();
        }

        /// <summary>
        /// 等待队列内已入队的事件全部处理完，供测试在 Raise 之后同步断言。
        /// </summary>
        public Task WaitForIdleAsync()
        {
            lock (this.syncRoot)
            {
                if (this.pendingCount == 0)
                {
                    return Task.CompletedTask;
                }

                this.idleSource ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return this.idleSource.Task;
            }
        }

        public void Dispose()
        {
            this.cancellation?.Cancel();
            this.cancellation?.Dispose();
            this.cancellation = null;
            GC.SuppressFinalize(this);
        }

        private async Task PumpAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (await this.channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (this.channel.Reader.TryRead(out var e))
                    {
                        try
                        {
                            await this.handler(e, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
#pragma warning disable CA1031
                        catch (Exception ex)
                        {
                            LogHandlerFailed(this.logger, e.Item?.Id ?? Guid.Empty, ex);
                        }
#pragma warning restore CA1031
                        finally
                        {
                            this.MarkProcessed();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void MarkProcessed()
        {
            TaskCompletionSource? completed = null;
            lock (this.syncRoot)
            {
                if (this.pendingCount > 0)
                {
                    this.pendingCount--;
                }

                if (this.pendingCount == 0 && this.idleSource != null)
                {
                    completed = this.idleSource;
                    this.idleSource = null;
                }
            }

            completed?.TrySetResult();
        }
    }
}
