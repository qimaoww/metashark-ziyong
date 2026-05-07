// <copyright file="LlmScrapeContextBuilder.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using Jellyfin.Plugin.MetaShark.Core;
    using Jellyfin.Plugin.MetaShark.Model;
    using MediaBrowser.Controller.Providers;

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Builder is intentionally injectable for assist service composition tests.")]
    public sealed class LlmScrapeContextBuilder
    {
        public LlmPromptContext Build(ItemLookupInfo info, string mediaType, IEnumerable<string?> libraryRoots)
        {
            ArgumentNullException.ThrowIfNull(info);
            var parsedName = ParseName(info, mediaType);
            return LlmPromptContextBuilder.Build(info, mediaType, libraryRoots, parsedName);
        }

        public string BuildJson(ItemLookupInfo info, string mediaType, IEnumerable<string?> libraryRoots)
        {
            ArgumentNullException.ThrowIfNull(info);
            var parsedName = ParseName(info, mediaType);
            return LlmPromptContextBuilder.BuildJson(info, mediaType, libraryRoots, parsedName);
        }

        private static ParseNameResult ParseName(ItemLookupInfo info, string mediaType)
        {
            var sourceName = string.IsNullOrWhiteSpace(info.Path) ? info.Name : System.IO.Path.GetFileNameWithoutExtension(info.Path);
            return string.Equals(mediaType, "Episode", StringComparison.OrdinalIgnoreCase)
                ? NameParser.ParseEpisode(sourceName ?? string.Empty)
                : NameParser.Parse(sourceName ?? string.Empty);
        }
    }
}
