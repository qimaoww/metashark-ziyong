// <copyright file="LlmPromptContextBuilder.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text.Json;
    using System.Text.RegularExpressions;
    using Jellyfin.Plugin.MetaShark.Model;
    using Jellyfin.Plugin.MetaShark.Providers;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;

    public static class LlmPromptContextBuilder
    {
        private const int MaxRelativePathSamples = 10;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            WriteIndented = false,
        };

        private static readonly string[] ExternalIdPromptConstraints =
        {
            "Return JSON only as { \"externalIdCandidates\": [...] }.",
            "Each external ID candidate must contain only provider, id, mediaType, confidence, reason, and evidence.",
            "Output only external ID candidates; do not output title, overview, people, images, URLs, local paths, Jellyfin item IDs, library roots, credentials, or configuration values.",
            "Allowed providers are TMDb, IMDb, TVDB, and Douban only.",
            "When the ID is unknown or confidence is insufficient, return { \"externalIdCandidates\": [] }.",
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

        public static LlmExternalIdPromptContext BuildExternalIdPromptContext(ItemLookupInfo info, string mediaType, IEnumerable<string?> libraryRoots, IEnumerable<string?>? relativePathSamples = null)
        {
            ArgumentNullException.ThrowIfNull(info);
            return new LlmExternalIdPromptContext
            {
                Task = "Resolve public external ID candidates for this media item.",
                OutputSchema = "{ \"externalIdCandidates\": [{ \"provider\": \"TMDb|IMDb|TVDB|Douban\", \"id\": \"public external id\", \"mediaType\": \"Movie|Series|Episode\", \"confidence\": 0.0, \"reason\": \"brief reason\", \"evidence\": \"brief evidence\" }] }",
                Constraints = ExternalIdPromptConstraints,
                MediaType = mediaType,
                Title = NormalizeText(info.Name),
                Year = NormalizePositiveNumber(info.Year),
                SeasonNumber = NormalizePositiveOrZeroNumber(info.ParentIndexNumber),
                EpisodeNumber = NormalizePositiveOrZeroNumber(info.IndexNumber),
                SafeRelativePathSamples = BuildSafeRelativePathSamples(info.Path, relativePathSamples, libraryRoots, mediaType),
                PublicProviderIds = BuildPublicProviderIds(info),
            };
        }

        public static string BuildExternalIdPromptJson(ItemLookupInfo info, string mediaType, IEnumerable<string?> libraryRoots, IEnumerable<string?>? relativePathSamples = null)
        {
            return JsonSerializer.Serialize(BuildExternalIdPromptContext(info, mediaType, libraryRoots, relativePathSamples), JsonOptions);
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

        private static string[] BuildSafeRelativePathSamples(string? itemPath, IEnumerable<string?>? relativePathSamples, IEnumerable<string?> libraryRoots, string mediaType)
        {
            return new[] { itemPath }
                .Concat(relativePathSamples ?? Array.Empty<string?>())
                .Select(path => NormalizeText(LlmRelativePathSanitizer.Sanitize(path, libraryRoots, mediaType)))
                .Where(sample => !string.IsNullOrWhiteSpace(sample))
                .Distinct(StringComparer.Ordinal)
                .Take(MaxRelativePathSamples)
                .Select(sample => sample!)
                .ToArray();
        }

        private static Dictionary<string, string> BuildPublicProviderIds(ItemLookupInfo info)
        {
            var publicProviderIds = new Dictionary<string, string>(StringComparer.Ordinal);
            ApplyPublicProviderIdDictionary(publicProviderIds, info.ProviderIds);

            if (info is EpisodeInfo episodeInfo)
            {
                ApplyPublicProviderIdDictionary(publicProviderIds, episodeInfo.SeriesProviderIds);
            }

            return publicProviderIds;
        }

        private static void ApplyPublicProviderIdDictionary(Dictionary<string, string> publicProviderIds, Dictionary<string, string>? providerIds)
        {
            if (providerIds == null)
            {
                return;
            }

            foreach (var providerId in providerIds)
            {
                if (TryNormalizePublicProviderId(providerId.Key, providerId.Value, out var publicKey, out var publicValue) && !publicProviderIds.ContainsKey(publicKey))
                {
                    publicProviderIds.Add(publicKey, publicValue);
                }
            }
        }

        private static bool TryNormalizePublicProviderId(string key, string value, out string publicKey, out string publicValue)
        {
            publicKey = string.Empty;
            publicValue = NormalizeText(value) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(publicValue))
            {
                return false;
            }

            if (string.Equals(key, MetadataProvider.Tmdb.ToString(), StringComparison.OrdinalIgnoreCase) || string.Equals(key, "TMDb", StringComparison.Ordinal))
            {
                publicKey = "TMDb";
                return IsPositiveNumericId(publicValue);
            }

            if (string.Equals(key, MetadataProvider.Imdb.ToString(), StringComparison.OrdinalIgnoreCase) || string.Equals(key, "IMDb", StringComparison.Ordinal))
            {
                publicKey = "IMDb";
                publicValue = publicValue.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? "tt" + publicValue[2..] : publicValue;
                return Regex.IsMatch(publicValue, @"^tt\d{7,}$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            }

            if (string.Equals(key, MetadataProvider.Tvdb.ToString(), StringComparison.OrdinalIgnoreCase) || string.Equals(key, "TVDB", StringComparison.Ordinal))
            {
                publicKey = "TVDB";
                return IsPositiveNumericId(publicValue);
            }

            if (string.Equals(key, BaseProvider.DoubanProviderId, StringComparison.OrdinalIgnoreCase) || string.Equals(key, "Douban", StringComparison.Ordinal))
            {
                publicKey = "Douban";
                return IsPositiveNumericId(publicValue);
            }

            return false;
        }

        private static bool IsPositiveNumericId(string value)
        {
            return Regex.IsMatch(value, @"^\d+$", RegexOptions.None, TimeSpan.FromMilliseconds(100))
                && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var numericValue)
                && numericValue > 0;
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
