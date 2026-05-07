// <copyright file="LlmExternalIdCandidate.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    public sealed class LlmExternalIdCandidate
    {
        public string? Provider { get; set; }

        public string? Id { get; set; }

        public string? MediaType { get; set; }

        public double Confidence { get; set; }

        public string? Reason { get; set; }

        public string? Evidence { get; set; }
    }
}
