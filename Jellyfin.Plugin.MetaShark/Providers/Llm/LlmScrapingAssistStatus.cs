// <copyright file="LlmScrapingAssistStatus.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    public enum LlmScrapingAssistStatus
    {
        NotTriggered = 0,
        Failed = 1,
        Succeeded = 2,
        Skipped = 3,
    }
}
