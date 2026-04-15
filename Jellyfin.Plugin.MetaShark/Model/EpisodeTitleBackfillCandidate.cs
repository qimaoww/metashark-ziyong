// <copyright file="EpisodeTitleBackfillCandidate.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Model
{
    using System;

    public class EpisodeTitleBackfillCandidate
    {
        public Guid ItemId { get; set; }

        public string ItemPath { get; set; } = string.Empty;

        public string OriginalTitleSnapshot { get; set; } = string.Empty;

        public string CandidateTitle { get; set; } = string.Empty;

        public DateTimeOffset QueuedAtUtc { get; set; }

        public DateTimeOffset NextAttemptAtUtc { get; set; }

        public int AttemptCount { get; set; }

        public string ClaimToken { get; set; } = string.Empty;

        public DateTimeOffset CreatedAtUtc
        {
            get => this.QueuedAtUtc;
            set => this.QueuedAtUtc = value;
        }

        public DateTimeOffset ExpiresAtUtc { get; set; }
    }
}
