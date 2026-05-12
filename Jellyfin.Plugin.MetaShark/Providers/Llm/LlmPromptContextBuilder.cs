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

        private static readonly string[] MetadataAllowedCandidateFields =
        {
            "mediaType",
            "title",
            "year",
            "seasonNumber",
            "episodeNumber",
            "originalTitle",
            "overview",
            "confidence",
        };

        private static readonly string[] MetadataForbiddenFields =
        {
            "sortTitle",
            "providerIds",
            "ProviderIds",
            "path",
            "absolutePath",
            "serverUrl",
            "apiKey",
            "token",
            "cookie",
        };

        private static readonly string[] MetadataPromptConstraints =
        {
            "Return exactly one JSON object and no Markdown, explanation, code fence, or natural-language prefix/suffix.",
            "The only top-level field is suggestions, and suggestions must be an array.",
            "Use only the allowed candidate fields listed in AllowedCandidateFields.",
            "Do not output reason; MetaShark metadata suggestions do not support a reason field.",
            "Do not output forbidden fields: sortTitle, providerIds, ProviderIds, path, absolutePath, serverUrl, apiKey, token, or cookie.",
            "confidence is required on every usable candidate and must be numeric from 0.0 to 1.0.",
            "If uncertain, return { \"suggestions\": [] } or use low confidence; do not omit confidence on usable candidates.",
            "For Episode media, suggest only current season/episode metadata. Do not change the series title, sort title, ProviderIds, or external IDs.",
            "DO: fill only blank or low-risk title, originalTitle, overview, year, seasonNumber, or episodeNumber fields for the current item.",
            "DO NOT: create or modify external IDs, ProviderIds, people, images, scraper mode, episode group mappings, refresh actions, or library configuration.",
            "If source facts are uncertain, conflict with known ProviderIds, or refer to a different work, return { \"suggestions\": [] }.",
            "Privacy: do not include full local paths, server URLs, Jellyfin private IDs, API keys, cookies, or tokens.",
        };

        private static readonly string[] ExternalIdPromptConstraints =
        {
            "Return JSON only as { \"externalIdCandidates\": [...] }.",
            "Each external ID candidate must contain only provider, id, mediaType, confidence, reason, and evidence.",
            "Output only external ID candidates; do not output title, overview, people, images, URLs, local paths, Jellyfin item IDs, library roots, credentials, or configuration values.",
            "Allowed providers are TMDb, IMDb, TVDB, and Douban only.",
            "DO: return only public external ID candidates that identify the exact same work, season, or episode as the current item.",
            "DO NOT: switch scraper mode, request TMDb metadata scraping, rewrite metadata fields, images, people, episode group mappings, or ProviderIds.",
            "DO NOT: replace an existing TMDb ID during ordinary missing-ID completion; only explicit TMDb correction evaluation may propose a replacement, and only when the candidate is strongly supported by public identifiers such as IMDb, TVDB, Douban, exact original title, year, and media type.",
            "DO NOT: remove, downgrade, or overwrite existing Douban, IMDb, TVDB, or TMDb identifiers.",
            "DO: when a default Douban metadata flow for a Movie or Series has only a Douban ID and no TMDb ID, return at most one matching TMDb ID as completion data only; keep its existing Douban metadata source; this is not a correction source.",
            "When PublicProviderIds contains Douban and TMDb is missing, return only one TMDb candidate or { \"externalIdCandidates\": [] }; do not return IMDb, TVDB, or Douban candidates.",
            "DO NOT: turn a Douban default flow into forced TMDb scraping, correction persistence, metadata replacement, image replacement, or a second refresh.",
            "If a Series already has a Douban ID but lacks a TMDb ID, resolve only the matching TMDb Series ID so the existing Douban metadata flow can keep its metadata source.",
            "If a Movie already has a Douban ID but lacks a TMDb ID, resolve only the matching TMDb Movie ID so the existing Douban metadata flow can keep its metadata source.",
            "If the exact public record cannot be verified, return { \"externalIdCandidates\": [] }; never guess or infer from popularity, loose title similarity, folder names, or partial matches.",
            "When PublicProviderIds contains Douban and TMDb is missing, any TMDb candidate must be the same work as that Douban subject and must match its title, original title, year, and media type.",
            "When localized Chinese titles differ between Douban and TMDb, use original title, year, media type, and public identifier evidence; do not accept or reject solely from localized title similarity.",
            "For Series, return only TMDb TV or TMDb Series IDs, never TMDb Movie IDs or unrelated works.",
            "For Movie, return only TMDb Movie IDs, never TMDb TV or Series IDs or unrelated works.",
            "The reason and evidence must name the matched Douban title, original title, year, and media type evidence when Douban is present; if that evidence is unavailable or conflicts, return { \"externalIdCandidates\": [] }.",
            "Do not map a Douban Series to a TMDb record with a different original title, different year, or different media type.",
            "When the ID is unknown or confidence is insufficient, return { \"externalIdCandidates\": [] }.",
        };

        public static LlmPromptContext Build(ItemLookupInfo info, string mediaType, IEnumerable<string?> libraryRoots, ParseNameResult? parsedName, bool allowRelativePathContext = true)
        {
            ArgumentNullException.ThrowIfNull(info);
            var relativePath = allowRelativePathContext ? LlmRelativePathSanitizer.Sanitize(info.Path, libraryRoots, mediaType) : string.Empty;
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
                ParsedName = allowRelativePathContext ? NormalizeText(parsedName?.Name) : null,
                ParsedChineseName = allowRelativePathContext ? NormalizeText(parsedName?.ChineseName) : null,
                ParsedYear = allowRelativePathContext ? NormalizePositiveNumber(parsedName?.Year) : null,
                ParsedSeasonNumber = allowRelativePathContext ? NormalizePositiveOrZeroNumber(parsedName?.ParentIndexNumber) : null,
                ParsedEpisodeNumber = allowRelativePathContext ? NormalizePositiveOrZeroNumber(parsedName?.IndexNumber) : null,
                ParsedIsSpecial = allowRelativePathContext && (parsedName?.IsSpecial ?? false),
                ParsedIsExtra = allowRelativePathContext && (parsedName?.IsExtra ?? false),
                ExistingProviderIdsSummary = BuildProviderIdsSummary(info),
            };
        }

        public static string BuildJson(ItemLookupInfo info, string mediaType, IEnumerable<string?> libraryRoots, ParseNameResult? parsedName, bool allowRelativePathContext = true)
        {
            return JsonSerializer.Serialize(Build(info, mediaType, libraryRoots, parsedName, allowRelativePathContext), JsonOptions);
        }

        public static string BuildMetadataAssistPromptJson(ItemLookupInfo info, string mediaType, IEnumerable<string?> libraryRoots, ParseNameResult? parsedName, bool allowRelativePathContext = true)
        {
            var prompt = new
            {
                Role = "Jellyfin metadata assistant.",
                Task = "Return only safe metadata suggestions for the current media item.",
                Output = "Exactly one JSON object, no Markdown, no explanation, no code fence, no natural-language prefix or suffix.",
                TopLevelField = "suggestions array",
                OutputSchema = "{ \"suggestions\": [{ \"mediaType\": \"Movie|Series|Season|Episode\", \"title\": \"safe title\", \"year\": 2024, \"seasonNumber\": 1, \"episodeNumber\": 1, \"originalTitle\": \"safe original title\", \"overview\": \"safe overview\", \"confidence\": 0.0 }] }",
                EmptyResultExample = "{ \"suggestions\": [] }",
                AllowedCandidateFields = MetadataAllowedCandidateFields,
                ForbiddenFields = MetadataForbiddenFields,
                Constraints = MetadataPromptConstraints,
                Context = Build(info, mediaType, libraryRoots, parsedName, allowRelativePathContext),
            };

            return JsonSerializer.Serialize(prompt, JsonOptions);
        }

        public static LlmExternalIdPromptContext BuildExternalIdPromptContext(ItemLookupInfo info, string mediaType, IEnumerable<string?> libraryRoots, IEnumerable<string?>? relativePathSamples = null, bool allowRelativePathContext = true)
        {
            ArgumentNullException.ThrowIfNull(info);
            return new LlmExternalIdPromptContext
            {
                Task = "Resolve public external ID candidates for this media item.",
                OutputSchema = "{ \"externalIdCandidates\": [{ \"provider\": \"TMDb|IMDb|TVDB|Douban\", \"id\": \"public external id\", \"mediaType\": \"Movie|Series|Episode\", \"confidence\": 0.0, \"reason\": \"brief reason\", \"evidence\": \"brief evidence\" }] }",
                Constraints = ExternalIdPromptConstraints,
                MediaType = mediaType,
                Title = NormalizeText(info.Name),
                OriginalTitle = NormalizeText(info.OriginalTitle),
                Year = NormalizePositiveNumber(info.Year),
                SeasonNumber = NormalizePositiveOrZeroNumber(info.ParentIndexNumber),
                EpisodeNumber = NormalizePositiveOrZeroNumber(info.IndexNumber),
                SafeRelativePathSamples = allowRelativePathContext ? BuildSafeRelativePathSamples(info.Path, relativePathSamples, libraryRoots, mediaType) : Array.Empty<string>(),
                PublicProviderIds = BuildPublicProviderIds(info),
            };
        }

        public static string BuildExternalIdPromptJson(ItemLookupInfo info, string mediaType, IEnumerable<string?> libraryRoots, IEnumerable<string?>? relativePathSamples = null, bool allowRelativePathContext = true)
        {
            return JsonSerializer.Serialize(BuildExternalIdPromptContext(info, mediaType, libraryRoots, relativePathSamples, allowRelativePathContext), JsonOptions);
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
