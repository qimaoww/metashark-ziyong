// <copyright file="EpisodeTitleBackfillCandidate.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Model
{
    using System;

    public class EpisodeTitleBackfillCandidate
    {
        public Guid ItemId { get; set; }

        public string OriginalTitleSnapshot { get; set; } = string.Empty;

        public string CandidateTitle { get; set; } = string.Empty;

        public DateTimeOffset CreatedAtUtc { get; set; }

        public DateTimeOffset ExpiresAtUtc { get; set; }
    }
}
