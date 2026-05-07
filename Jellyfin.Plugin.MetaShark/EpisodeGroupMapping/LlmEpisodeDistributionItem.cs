// <copyright file="LlmEpisodeDistributionItem.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    public sealed class LlmEpisodeDistributionItem
    {
        public int SeasonNumber { get; set; }

        public int EpisodeCount { get; set; }
    }
}
