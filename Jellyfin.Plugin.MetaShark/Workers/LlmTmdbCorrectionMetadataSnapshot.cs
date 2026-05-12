// <copyright file="LlmTmdbCorrectionMetadataSnapshot.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers
{
    using System;
    using System.Collections.Generic;

    public sealed class LlmTmdbCorrectionMetadataSnapshot
    {
        public Guid ItemId { get; set; }

        public string ItemPath { get; set; } = string.Empty;

        public string MediaType { get; set; } = string.Empty;

        public string TmdbId { get; set; } = string.Empty;

        public string? Name { get; set; }

        public string? OriginalTitle { get; set; }

        public string? Overview { get; set; }

        public int? ProductionYear { get; set; }

        public DateTime? PremiereDate { get; set; }

        public Dictionary<string, string> ProviderIds { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public DateTimeOffset QueuedAtUtc { get; set; }

        public DateTimeOffset ExpiresAtUtc { get; set; }
    }
}
