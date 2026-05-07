// <copyright file="LlmExternalIdResolutionStatus.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    public enum LlmExternalIdResolutionStatus
    {
        NotTriggered = 0,
        Skipped = 1,
        Succeeded = 2,
        Rejected = 3,
        ValidationFailed = 4,
        VerificationFailed = 5,
    }
}
