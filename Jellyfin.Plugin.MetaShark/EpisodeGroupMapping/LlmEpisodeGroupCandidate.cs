// <copyright file="LlmEpisodeGroupCandidate.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    public sealed class LlmEpisodeGroupCandidate
    {
        public string? GroupId { get; set; }

        public string? Name { get; set; }

        public string? Type { get; set; }

        public int? GroupCount { get; set; }

        public int? EpisodeCount { get; set; }
    }
}
