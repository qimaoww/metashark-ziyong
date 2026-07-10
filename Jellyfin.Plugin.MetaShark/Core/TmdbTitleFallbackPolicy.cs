// <copyright file="TmdbTitleFallbackPolicy.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Core
{
    using System;

    /// <summary>
    /// Resolves same-region TMDb title fallbacks for movies and series.
    /// </summary>
    public static class TmdbTitleFallbackPolicy
    {
        public static bool ShouldTryRegionalAlternativeTitle(
            string? resolvedLanguage,
            string? detailsTitle,
            string? originalTitle,
            string? originalLanguage)
        {
            if (ChineseLocalePolicy.GetTmdbChineseRegionCode(resolvedLanguage) == null
                || ChineseLocalePolicy.IsChineseRequest(originalLanguage))
            {
                return false;
            }

            var details = Normalize(detailsTitle);
            var original = Normalize(originalTitle);
            return details == null
                || (original != null && string.Equals(details, original, StringComparison.Ordinal));
        }

        public static string? ResolveTitle(string? detailsTitle, string? originalTitle, string? regionalAlternativeTitle)
        {
            return Normalize(regionalAlternativeTitle) ?? Normalize(detailsTitle) ?? Normalize(originalTitle);
        }

        private static string? Normalize(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
