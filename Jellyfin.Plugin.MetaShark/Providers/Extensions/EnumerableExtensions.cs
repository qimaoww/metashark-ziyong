// <copyright file="EnumerableExtensions.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Jellyfin.Plugin.MetaShark.Core;
    using MediaBrowser.Model.Providers;

    public static class EnumerableExtensions
    {
        private const int MaxPriority = 99;

        public static IEnumerable<RemoteImageInfo> OrderByLanguageDescending(this IEnumerable<RemoteImageInfo> remoteImageInfos, params string[] requestedLanguages)
        {
            ArgumentNullException.ThrowIfNull(remoteImageInfos);
            ArgumentNullException.ThrowIfNull(requestedLanguages);
            if (requestedLanguages.Length <= 0)
            {
                requestedLanguages = new[] { "en" };
            }

            var requestedLanguagePriorityMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var genericRequestedLanguagePriorityMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < requestedLanguages.Length; i++)
            {
                if (string.IsNullOrEmpty(requestedLanguages[i]))
                {
                    continue;
                }

                var normalizedLanguage = NormalizeLanguage(requestedLanguages[i]);
                if (string.IsNullOrEmpty(normalizedLanguage) || requestedLanguagePriorityMap.ContainsKey(normalizedLanguage))
                {
                    continue;
                }

                requestedLanguagePriorityMap.Add(normalizedLanguage, MaxPriority - i);
                var genericLanguage = GetGenericLanguage(normalizedLanguage);
                if (!string.Equals(genericLanguage, normalizedLanguage, StringComparison.OrdinalIgnoreCase)
                    && !genericRequestedLanguagePriorityMap.ContainsKey(genericLanguage))
                {
                    genericRequestedLanguagePriorityMap.Add(genericLanguage, (MaxPriority - i) - requestedLanguages.Length);
                }
            }

            return remoteImageInfos.OrderByDescending(delegate(RemoteImageInfo i)
            {
                if (string.IsNullOrEmpty(i.Language))
                {
                    return 3;
                }

                var normalizedImageLanguage = NormalizeLanguage(i.Language);
                if (requestedLanguagePriorityMap.TryGetValue(normalizedImageLanguage, out int priority))
                {
                    return priority;
                }

                if (string.Equals(normalizedImageLanguage, GetGenericLanguage(normalizedImageLanguage), StringComparison.OrdinalIgnoreCase)
                    && genericRequestedLanguagePriorityMap.TryGetValue(normalizedImageLanguage, out priority))
                {
                    return priority;
                }

                return string.Equals(normalizedImageLanguage, "en", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
            }).ThenByDescending((RemoteImageInfo i) => i.CommunityRating.GetValueOrDefault()).ThenByDescending((RemoteImageInfo i) => i.VoteCount.GetValueOrDefault());
        }

        private static string NormalizeLanguage(string language)
        {
            if (string.IsNullOrEmpty(language))
            {
                return language;
            }

            return ChineseLocalePolicy.CanonicalizeLanguage(language) ?? language;
        }

        private static string GetGenericLanguage(string language)
        {
            if (string.IsNullOrEmpty(language))
            {
                return language;
            }

            return language.Split('-')[0].ToLowerInvariant();
        }
    }
}
