// <copyright file="EpisodeTitleBackfillPolicy.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using Jellyfin.Plugin.MetaShark.Core;
    using Jellyfin.Plugin.MetaShark.Model;

    public static class EpisodeTitleBackfillPolicy
    {
        public static string? ResolveEpisodeTitlePersistence(string? originalMetadataTitle, EpisodeLocalizedValue? providerTitle)
        {
            // 中文标题校验不能依赖原名是否恰好为“第 N 集”：
            // 新入库和覆盖刷新时，原名也可能来自文件名中的整部剧名。
            if (HasChineseMetadataTitleSource(providerTitle) || IsDefaultJellyfinEpisodeTitle(originalMetadataTitle))
            {
                return IsUsableChineseEpisodeTitle(providerTitle)
                    ? providerTitle!.Value!.Trim()
                    : originalMetadataTitle;
            }

            return string.IsNullOrWhiteSpace(providerTitle?.Value) ? originalMetadataTitle : providerTitle.Value;
        }

        public static bool IsGenericTmdbEpisodeTitle(string? title)
        {
            var trimmedTitle = title?.Trim();
            if (string.IsNullOrWhiteSpace(trimmedTitle))
            {
                return false;
            }

            if (trimmedTitle.StartsWith('第')
                && (trimmedTitle.EndsWith('集')
                    || trimmedTitle.EndsWith('话')
                    || trimmedTitle.EndsWith('話')))
            {
                var chineseNumericPart = trimmedTitle[1..^1].Trim();
                return chineseNumericPart.Length > 0 && chineseNumericPart.All(char.IsDigit);
            }

            if (!trimmedTitle.StartsWith("Episode ", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var numericPart = trimmedTitle.Substring("Episode ".Length).Trim();
            return numericPart.Length > 0 && numericPart.All(char.IsDigit);
        }

        public static bool IsDefaultJellyfinEpisodeTitle(string? title)
        {
            if (string.IsNullOrEmpty(title) || !title.StartsWith("第 ", StringComparison.Ordinal) || !title.EndsWith(" 集", StringComparison.Ordinal))
            {
                return false;
            }

            var numericPart = title.Substring(2, title.Length - 4);
            if (numericPart.Length == 0 || numericPart[0] == '0')
            {
                return false;
            }

            foreach (var character in numericPart)
            {
                if (!char.IsDigit(character))
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool IsUsableChineseEpisodeTitle(EpisodeLocalizedValue? providerTitle)
        {
            return HasChineseMetadataTitleSource(providerTitle)
                && !IsGenericTmdbEpisodeTitle(providerTitle?.Value)
                && ChineseLocalePolicy.IsTextAllowedForChineseMetadataLanguage(providerTitle?.Value, providerTitle?.SourceLanguage);
        }

        internal static string ResolveChineseEpisodeTitleFallback(
            int episodeNumber,
            string? originalMetadataTitle,
            string? currentMetadataTitle,
            string? metadataLanguage,
            IEnumerable<string?> parentTitles,
            IEnumerable<string?> fileNameTitles)
        {
            // 已保存且符合目标中文语言的单集名可以与文件名相同，
            // 不能仅凭文件名就认定它是剧名；已知的父级剧名/季名仍然拒绝。
            if (!string.IsNullOrWhiteSpace(currentMetadataTitle)
                && !IsGenericTmdbEpisodeTitle(currentMetadataTitle)
                && ChineseLocalePolicy.IsTextAllowedForChineseMetadataLanguage(currentMetadataTitle, metadataLanguage)
                && !IsKnownNonEpisodeTitle(currentMetadataTitle, parentTitles))
            {
                return currentMetadataTitle.Trim();
            }

            // 其余标题不能使用父级标题或文件名解析出的剧名作为回退。
            var nonEpisodeTitles = parentTitles.Concat(fileNameTitles);
            foreach (var title in new[] { currentMetadataTitle, originalMetadataTitle })
            {
                if (!string.IsNullOrWhiteSpace(title)
                    && !IsGenericTmdbEpisodeTitle(title)
                    && !IsKnownNonEpisodeTitle(title, nonEpisodeTitles))
                {
                    return title.Trim();
                }
            }

            if (IsGenericTmdbEpisodeTitle(originalMetadataTitle))
            {
                return originalMetadataTitle!.Trim();
            }

            return string.Format(CultureInfo.InvariantCulture, "第 {0} 集", episodeNumber);
        }

        internal static bool IsKnownNonEpisodeTitle(string? title, IEnumerable<string?> nonEpisodeTitles)
        {
            var normalizedTitle = NormalizeTitleForComparison(title);
            return normalizedTitle.Length > 0
                && nonEpisodeTitles.Any(parentTitle => string.Equals(
                    normalizedTitle,
                    NormalizeTitleForComparison(parentTitle),
                    StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeTitleForComparison(string? title)
        {
            return string.Concat((title ?? string.Empty).Where(char.IsLetterOrDigit));
        }

        private static bool HasChineseMetadataTitleSource(EpisodeLocalizedValue? providerTitle)
        {
            return ChineseLocalePolicy.IsChineseMetadataLanguage(providerTitle?.SourceLanguage);
        }
    }
}
