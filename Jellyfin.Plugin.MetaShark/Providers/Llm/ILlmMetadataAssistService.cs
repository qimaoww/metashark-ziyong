// <copyright file="ILlmMetadataAssistService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System.Threading;
    using System.Threading.Tasks;

    public interface ILlmMetadataAssistService
    {
        Task<LlmScrapingAssistResult> AssistAsync(LlmScrapingAssistRequest request, CancellationToken cancellationToken);
    }
}
