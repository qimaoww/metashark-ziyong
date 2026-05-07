// <copyright file="ILlmEpisodeGroupMappingProviderAssistService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    using System.Threading;
    using System.Threading.Tasks;

    public interface ILlmEpisodeGroupMappingProviderAssistService
    {
        Task<LlmEpisodeGroupMappingAssistResult> SuggestWriteAndRefreshAsync(LlmEpisodeGroupMappingProviderAssistRequest request, CancellationToken cancellationToken);
    }
}
