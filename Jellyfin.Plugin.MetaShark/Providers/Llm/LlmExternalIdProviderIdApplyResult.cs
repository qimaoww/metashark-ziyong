// <copyright file="LlmExternalIdProviderIdApplyResult.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System.Collections.Generic;

    public sealed class LlmExternalIdProviderIdApplyResult
    {
        public LlmExternalIdProviderIdApplyResult(
            IReadOnlyList<LlmExternalIdProviderIdWrite> appliedWrites,
            IReadOnlyList<LlmExternalIdProviderIdWrite> skippedWrites,
            IReadOnlyList<string> diagnostics)
        {
            this.AppliedWrites = appliedWrites;
            this.SkippedWrites = skippedWrites;
            this.Diagnostics = diagnostics;
        }

        public IReadOnlyList<LlmExternalIdProviderIdWrite> AppliedWrites { get; }

        public IReadOnlyList<LlmExternalIdProviderIdWrite> SkippedWrites { get; }

        public IReadOnlyList<string> Diagnostics { get; }
    }
}
