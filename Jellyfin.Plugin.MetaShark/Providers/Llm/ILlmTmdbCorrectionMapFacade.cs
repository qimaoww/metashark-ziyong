// <copyright file="ILlmTmdbCorrectionMapFacade.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using Jellyfin.Plugin.MetaShark.Configuration;

    public interface ILlmTmdbCorrectionMapFacade
    {
        bool TryGetCorrection(PluginConfiguration configuration, string mediaType, string? doubanId, out string tmdbId);

        bool TryGetCompletion(PluginConfiguration configuration, string mediaType, string? doubanId, out string tmdbId);

        bool TryFindCorrectionByTmdbId(PluginConfiguration configuration, string mediaType, string? tmdbId, out string correctedTmdbId);
    }
}
