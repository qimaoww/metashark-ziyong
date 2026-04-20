// <copyright file="TvMissingImageRefillScanSummary.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    public sealed class TvMissingImageRefillScanSummary
    {
        public TvMissingImageRefillScanSummary(int candidateCount, int queuedCount, int skippedCount, string? skippedReasons)
        {
            this.CandidateCount = candidateCount;
            this.QueuedCount = queuedCount;
            this.SkippedCount = skippedCount;
            this.SkippedReasons = string.IsNullOrWhiteSpace(skippedReasons) ? "None" : skippedReasons;
        }

        public int CandidateCount { get; }

        public int QueuedCount { get; }

        public int SkippedCount { get; }

        public string SkippedReasons { get; }
    }
}
