// <copyright file="LlmScrapingAssistRequest.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Collections.Generic;
    using Jellyfin.Plugin.MetaShark.Configuration;
    using MediaBrowser.Controller.Providers;
    using Microsoft.AspNetCore.Http;

    public sealed class LlmScrapingAssistRequest
    {
        public PluginConfiguration? Configuration { get; set; }

        public ItemLookupInfo? LookupInfo { get; set; }

        public string? MediaType { get; set; }

        public DefaultScraperSemantic Semantic { get; set; }

        public bool IsImageProvider { get; set; }

        public HttpContext? HttpContext { get; set; }

        public IEnumerable<string?> LibraryRoots { get; set; } = Array.Empty<string?>();
    }
}
