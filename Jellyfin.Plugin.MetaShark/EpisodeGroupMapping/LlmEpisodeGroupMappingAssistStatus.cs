// <copyright file="LlmEpisodeGroupMappingAssistStatus.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    public enum LlmEpisodeGroupMappingAssistStatus
    {
        NotTriggered,
        Failed,
        Rejected,
        NoChange,
        Updated,
    }
}
