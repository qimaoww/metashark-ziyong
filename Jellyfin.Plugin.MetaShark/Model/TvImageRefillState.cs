// <copyright file="TvImageRefillState.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Model
{
    using System;

    public class TvImageRefillState
    {
        public Guid ItemId { get; set; }

        public string Fingerprint { get; set; } = string.Empty;

        public TvImageRefillStatus Status { get; set; }

        public int AttemptCount { get; set; }

        public string LastReason { get; set; } = string.Empty;

        public DateTimeOffset? NextRetryAtUtc { get; set; }

        public DateTimeOffset UpdatedAtUtc { get; set; }
    }
}
