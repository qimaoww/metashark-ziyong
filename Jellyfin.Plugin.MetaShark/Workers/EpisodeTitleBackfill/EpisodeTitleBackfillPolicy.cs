// <copyright file="EpisodeTitleBackfillPolicy.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill
{
    using System;
    using System.Linq;
    using Jellyfin.Plugin.MetaShark.Model;

    public static class EpisodeTitleBackfillPolicy
    {
        public static string? ResolveEpisodeTitlePersistence(string? originalMetadataTitle, EpisodeLocalizedValue? providerTitle)
        {
            if (!IsDefaultJellyfinEpisodeTitle(originalMetadataTitle))
            {
                return providerTitle?.Value;
            }

            if (!HasStrictZhCnTitleSource(providerTitle))
            {
                return originalMetadataTitle;
            }

            var trimmedProviderTitle = providerTitle?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(trimmedProviderTitle))
            {
                return originalMetadataTitle;
            }

            if (IsGenericTmdbEpisodeTitle(trimmedProviderTitle))
            {
                return originalMetadataTitle;
            }

            return trimmedProviderTitle;
        }

        public static bool IsGenericTmdbEpisodeTitle(string? title)
        {
            var trimmedTitle = title?.Trim();
            if (string.IsNullOrWhiteSpace(trimmedTitle))
            {
                return false;
            }

            if (IsDefaultJellyfinEpisodeTitle(trimmedTitle))
            {
                return true;
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

        private static bool HasStrictZhCnTitleSource(EpisodeLocalizedValue? providerTitle)
        {
            return string.Equals(providerTitle?.SourceLanguage?.Trim(), "zh-CN", StringComparison.OrdinalIgnoreCase);
        }
    }
}
