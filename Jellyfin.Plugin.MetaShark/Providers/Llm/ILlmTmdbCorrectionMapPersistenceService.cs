// <copyright file="ILlmTmdbCorrectionMapPersistenceService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System.Threading;
    using System.Threading.Tasks;

    public interface ILlmTmdbCorrectionMapPersistenceService
    {
        Task<LlmTmdbCorrectionMapPersistenceResult> TryUpsertDoubanCorrectionAsync(string mediaType, string doubanId, string tmdbId, CancellationToken cancellationToken);

        Task<LlmTmdbCorrectionMapPersistenceResult> TryUpsertDoubanCompletionAsync(string mediaType, string doubanId, string tmdbId, CancellationToken cancellationToken);
    }
}
