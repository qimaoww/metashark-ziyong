// <copyright file="EpisodeGroupMappingFacade.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    using System;
    using Jellyfin.Plugin.MetaShark.Configuration;
    using Jellyfin.Plugin.MetaShark.Core;

    public sealed class EpisodeGroupMappingFacade : IEpisodeGroupMappingFacade
    {
        public bool TryGetEffectiveGroupId(PluginConfiguration? configuration, string? tmdbSeriesId, out string groupId)
        {
            groupId = string.Empty;
            if (configuration == null)
            {
                return false;
            }

            return this.TryGetManualGroupId(configuration, tmdbSeriesId, out groupId)
                || EpisodeGroupMapParser.Shared.TryGetGroupId(configuration.LlmTmdbEpisodeGroupMap, tmdbSeriesId, out groupId);
        }

        public bool TryGetManualGroupId(PluginConfiguration? configuration, string? tmdbSeriesId, out string groupId)
        {
            groupId = string.Empty;
            if (configuration == null)
            {
                return false;
            }

            return EpisodeGroupMapParser.Shared.TryGetGroupId(configuration.TmdbEpisodeGroupMap, tmdbSeriesId, out groupId);
        }

        public string GetEffectiveMappingText(PluginConfiguration? configuration)
        {
            if (configuration == null)
            {
                return string.Empty;
            }

            return this.GetEffectiveMappingText(configuration.TmdbEpisodeGroupMap, configuration.LlmTmdbEpisodeGroupMap);
        }

        public string GetEffectiveMappingText(string? manualMapping, string? llmMapping)
        {
            return TmdbEpisodeGroupMapping.GetEffectiveMappingText(manualMapping, llmMapping);
        }
    }
}
