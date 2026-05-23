// <copyright file="IEpisodeGroupMappingFacade.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    using Jellyfin.Plugin.MetaShark.Configuration;

    public interface IEpisodeGroupMappingFacade
    {
        bool TryGetEffectiveGroupId(PluginConfiguration? configuration, string? tmdbSeriesId, out string groupId);

        bool TryGetManualGroupId(PluginConfiguration? configuration, string? tmdbSeriesId, out string groupId);

        string GetEffectiveMappingText(PluginConfiguration? configuration);

        string GetEffectiveMappingText(string? manualMapping, string? llmMapping);
    }
}
