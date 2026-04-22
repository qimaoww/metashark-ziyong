// <copyright file="IPersonMissingImageRefillService.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Threading;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Library;

    public interface IPersonMissingImageRefillService
    {
        PersonMissingImageRefillScanSummary QueueMissingImagesForFullLibraryScan(CancellationToken cancellationToken);

        void QueueMissingImagesForUpdatedItem(ItemChangeEventArgs e, CancellationToken cancellationToken);

        void MarkCompleted(Person person, string reason);

        void MarkRetryable(Person person, string reason, DateTimeOffset? nextRetryAtUtc = null);
    }

    public sealed class PersonMissingImageRefillScanSummary
    {
        public PersonMissingImageRefillScanSummary(int candidateCount, int queuedCount, int skippedCount, string? skippedReasons)
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
