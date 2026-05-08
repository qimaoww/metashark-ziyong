// <copyright file="LlmRequestLimiter.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed class LlmRequestLimiter : ILlmRequestLimiter, IDisposable
    {
        public const int DefaultMaxConcurrency = 1;

        private readonly SemaphoreSlim semaphore;

        public LlmRequestLimiter()
            : this(DefaultMaxConcurrency)
        {
        }

        public LlmRequestLimiter(int maxConcurrency)
        {
            this.MaxConcurrency = Math.Max(1, maxConcurrency);
            this.semaphore = new SemaphoreSlim(this.MaxConcurrency, this.MaxConcurrency);
        }

        public int MaxConcurrency { get; }

        public async Task<IDisposable?> TryAcquireAsync(CancellationToken cancellationToken)
        {
            var acquired = await this.semaphore.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            return acquired ? new Lease(this.semaphore) : null;
        }

        public void Dispose()
        {
            this.semaphore.Dispose();
        }

        private sealed class Lease : IDisposable
        {
            private readonly SemaphoreSlim semaphore;
            private int disposed;

            public Lease(SemaphoreSlim semaphore)
            {
                this.semaphore = semaphore;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref this.disposed, 1) == 0)
                {
                    this.semaphore.Release();
                }
            }
        }
    }
}
