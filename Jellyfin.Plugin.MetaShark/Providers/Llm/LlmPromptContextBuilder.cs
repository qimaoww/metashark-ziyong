// <copyright file="LlmPromptContextBuilder.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using Jellyfin.Plugin.MetaShark.Model;
    using Jellyfin.Plugin.MetaShark.Providers;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;

    public static class LlmPromptContextBuilder
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            WriteIndented = false,
        };

        public static LlmPromptContext Build(ItemLookupInfo info, string mediaType, IEnumerable<string?> libraryRoots, ParseNameResult? parsedName)
        {
            ArgumentNullException.ThrowIfNull(info);
            var relativePath = LlmRelativePathSanitizer.Sanitize(info.Path, libraryRoots, mediaType);
            return new LlmPromptContext
            {
                MediaType = mediaType,
                Name = NormalizeText(info.Name),
                RelativePath = relativePath,
                FileName = GetFileName(relativePath),
                ParentFolderName = GetParentFolderName(relativePath),
                MetadataLanguage = NormalizeText(info.MetadataLanguage),
                Year = NormalizePositiveNumber(info.Year),
                SeasonNumber = NormalizePositiveOrZeroNumber(info.ParentIndexNumber),
                EpisodeNumber = NormalizePositiveOrZeroNumber(info.IndexNumber),
                SeriesDisplayOrder = NormalizeText(GetSeriesDisplayOrder(info)),
                ParsedName = NormalizeText(parsedName?.Name),
                ParsedChineseName = NormalizeText(parsedName?.ChineseName),
                ParsedYear = NormalizePositiveNumber(parsedName?.Year),
                ParsedSeasonNumber = NormalizePositiveOrZeroNumber(parsedName?.ParentIndexNumber),
                ParsedEpisodeNumber = NormalizePositiveOrZeroNumber(parsedName?.IndexNumber),
                ParsedIsSpecial = parsedName?.IsSpecial ?? false,
                ParsedIsExtra = parsedName?.IsExtra ?? false,
                ExistingProviderIdsSummary = BuildProviderIdsSummary(info),
            };
        }

        public static string BuildJson(ItemLookupInfo info, string mediaType, IEnumerable<string?> libraryRoots, ParseNameResult? parsedName)
        {
            return JsonSerializer.Serialize(Build(info, mediaType, libraryRoots, parsedName), JsonOptions);
        }

        private static LlmProviderIdsSummary BuildProviderIdsSummary(ItemLookupInfo info)
        {
            var summary = new LlmProviderIdsSummary();
            ApplyProviderIdDictionary(summary, info.ProviderIds);

            if (info is EpisodeInfo episodeInfo)
            {
                ApplyProviderIdDictionary(summary, episodeInfo.SeriesProviderIds);
            }

            return summary;
        }

        private static void ApplyProviderIdDictionary(LlmProviderIdsSummary summary, Dictionary<string, string>? providerIds)
        {
            if (providerIds == null)
            {
                return;
            }

            summary.Douban |= providerIds.ContainsKey(BaseProvider.DoubanProviderId);
            summary.Tmdb |= providerIds.ContainsKey(MetadataProvider.Tmdb.ToString());
            summary.Tvdb |= providerIds.ContainsKey(MetadataProvider.Tvdb.ToString());
            summary.Imdb |= providerIds.ContainsKey(MetadataProvider.Imdb.ToString());
        }

        private static string? GetSeriesDisplayOrder(ItemLookupInfo info)
        {
            return info is EpisodeInfo episodeInfo ? episodeInfo.SeriesDisplayOrder : null;
        }

        private static string? GetFileName(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return null;
            }

            return Path.GetFileName(relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static string? GetParentFolderName(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return null;
            }

            var normalizedPath = relativePath.Replace('/', Path.DirectorySeparatorChar);
            var parentPath = Path.GetDirectoryName(normalizedPath);
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return null;
            }

            return Path.GetFileName(parentPath);
        }

        private static string? NormalizeText(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static int? NormalizePositiveNumber(int? value)
        {
            return value > 0 ? value : null;
        }

        private static int? NormalizePositiveOrZeroNumber(int? value)
        {
            return value >= 0 ? value : null;
        }
    }
}
