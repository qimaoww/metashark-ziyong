// <copyright file="TvImageRefillOutcomeReporter.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using Jellyfin.Plugin.MetaShark.Model;
    using MediaBrowser.Controller.Entities;

    public sealed class TvImageRefillOutcomeReporter : ITvImageRefillOutcomeReporter
    {
        private readonly ITvImageRefillStateStore stateStore;

        public TvImageRefillOutcomeReporter(ITvImageRefillStateStore stateStore)
        {
            this.stateStore = stateStore;
        }

        public void ReportHardMiss(BaseItem item, string reason)
        {
            ArgumentNullException.ThrowIfNull(item);

            if (item.Id == Guid.Empty)
            {
                return;
            }

            var current = this.stateStore.GetState(item.Id);
            this.stateStore.Save(new TvImageRefillState
            {
                ItemId = item.Id,
                Fingerprint = TvImageRefillFingerprint.Create(item),
                Status = TvImageRefillStatus.HardMiss,
                AttemptCount = current?.AttemptCount ?? 0,
                LastReason = reason,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
        }

        public void ReportSuccess(BaseItem item)
        {
            ArgumentNullException.ThrowIfNull(item);

            if (item.Id == Guid.Empty)
            {
                return;
            }

            this.stateStore.Remove(item.Id);
        }

        public void ReportTransientFailure(BaseItem item, string reason)
        {
            ArgumentNullException.ThrowIfNull(item);

            if (item.Id == Guid.Empty)
            {
                return;
            }

            var current = this.stateStore.GetState(item.Id);
            this.stateStore.Save(new TvImageRefillState
            {
                ItemId = item.Id,
                Fingerprint = TvImageRefillFingerprint.Create(item),
                Status = TvImageRefillStatus.CoolingDown,
                AttemptCount = (current?.AttemptCount ?? 0) + 1,
                LastReason = reason,
                NextRetryAtUtc = DateTimeOffset.UtcNow.AddMinutes(30),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
        }
    }
}
