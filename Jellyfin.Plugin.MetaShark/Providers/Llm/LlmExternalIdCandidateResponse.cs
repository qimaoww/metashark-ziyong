// <copyright file="LlmExternalIdCandidateResponse.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    public sealed class LlmExternalIdCandidateResponse
    {
        [JsonPropertyName("externalIdCandidates")]
        public IEnumerable<LlmExternalIdCandidate> ExternalIdCandidates { get; set; } = Array.Empty<LlmExternalIdCandidate>();
    }
}
