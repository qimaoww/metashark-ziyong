// <copyright file="TvdbEpisode.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Api
{
    using System;

    public sealed class TvdbEpisode
    {
        public int? Id { get; set; }

        public int? SeasonNumber { get; set; }

        public int? Number { get; set; }

        public int? AirsBeforeSeason { get; set; }

        public int? AirsBeforeEpisode { get; set; }

        public int? AirsAfterSeason { get; set; }

        public DateTime? Aired { get; set; }
    }
}
