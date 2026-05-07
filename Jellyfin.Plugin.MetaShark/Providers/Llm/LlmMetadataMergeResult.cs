// <copyright file="LlmMetadataMergeResult.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Collections.Generic;

    public sealed class LlmMetadataMergeResult
    {
        private LlmMetadataMergeResult(bool applied, string reason, IReadOnlyList<string> changedFields)
        {
            this.Applied = applied;
            this.Reason = reason;
            this.ChangedFields = changedFields;
        }

        public bool Applied { get; }

        public string Reason { get; }

        public IReadOnlyList<string> ChangedFields { get; }

        public static LlmMetadataMergeResult AppliedResult(IReadOnlyList<string> changedFields)
        {
            return new LlmMetadataMergeResult(true, "Applied", changedFields ?? Array.Empty<string>());
        }

        public static LlmMetadataMergeResult Skipped(string reason)
        {
            return new LlmMetadataMergeResult(false, reason, Array.Empty<string>());
        }
    }
}
