// <copyright file="LlmAssistTriggerContext.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using Jellyfin.Plugin.MetaShark.Configuration;
    using Microsoft.AspNetCore.Http;

    public sealed class LlmAssistTriggerContext
    {
        public PluginConfiguration? Configuration { get; set; }

        public DefaultScraperSemantic Semantic { get; set; }

        public string? MediaType { get; set; }

        public bool IsImageProvider { get; set; }

        public HttpContext? HttpContext { get; set; }

        public bool HasBridgedExplicitSearchMissingMetadataRefreshIntent { get; set; }

        public bool HasBridgedExplicitOverwriteMetadataRefreshIntent { get; set; }

        public bool AllowOverwriteRefresh { get; set; }
    }
}
