// <copyright file="PersonImageRefillState.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Model
{
    using System;

    public class PersonImageRefillState
    {
        public Guid PersonId { get; set; }

        public string Fingerprint { get; set; } = string.Empty;

        public PersonImageRefillStatus Status { get; set; }

        public int AttemptCount { get; set; }

        public string LastReason { get; set; } = string.Empty;

        public DateTimeOffset? NextRetryAtUtc { get; set; }

        public DateTimeOffset UpdatedAtUtc { get; set; }
    }
}
