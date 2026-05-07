// <copyright file="ILlmApi.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Api
{
    using System.Threading;
    using System.Threading.Tasks;

    public interface ILlmApi
    {
        Task<LlmApiResult> CompleteAsync(string prompt, CancellationToken cancellationToken);
    }
}
