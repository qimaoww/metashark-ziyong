// <copyright file="ILlmRequestLimiter.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    public interface ILlmRequestLimiter
    {
        int MaxConcurrency { get; }

        Task<IDisposable?> TryAcquireAsync(CancellationToken cancellationToken);
    }
}
