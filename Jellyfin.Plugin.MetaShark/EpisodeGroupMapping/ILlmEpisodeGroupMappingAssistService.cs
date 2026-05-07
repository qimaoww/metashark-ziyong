// <copyright file="ILlmEpisodeGroupMappingAssistService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    using System.Threading;
    using System.Threading.Tasks;

    public interface ILlmEpisodeGroupMappingAssistService
    {
        Task<LlmEpisodeGroupMappingAssistResult> SuggestAndWriteAsync(LlmEpisodeGroupMappingAssistRequest request, CancellationToken cancellationToken);
    }
}
