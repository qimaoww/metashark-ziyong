// <copyright file="ProviderIdSnapshot.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers
{
    using System;
    using System.Collections.Generic;
    using MediaBrowser.Model.Entities;

    internal static class ProviderIdSnapshot
    {
        public static Dictionary<string, string>? CreatePublicProviderIdCopy(Dictionary<string, string>? providerIds)
        {
            if (providerIds == null)
            {
                return null;
            }

            var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var providerId in providerIds)
            {
                if (TryNormalizePublicProviderIdKey(providerId.Key, out var key))
                {
                    copy[key] = providerId.Value;
                }
            }

            return copy;
        }

        private static bool TryNormalizePublicProviderIdKey(string key, out string normalizedKey)
        {
            if (string.Equals(key, MetadataProvider.Tmdb.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                normalizedKey = MetadataProvider.Tmdb.ToString();
                return true;
            }

            if (string.Equals(key, MetadataProvider.Imdb.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                normalizedKey = MetadataProvider.Imdb.ToString();
                return true;
            }

            if (string.Equals(key, MetadataProvider.Tvdb.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                normalizedKey = MetadataProvider.Tvdb.ToString();
                return true;
            }

            if (string.Equals(key, BaseProvider.DoubanProviderId, StringComparison.OrdinalIgnoreCase))
            {
                normalizedKey = BaseProvider.DoubanProviderId;
                return true;
            }

            normalizedKey = string.Empty;
            return false;
        }
    }
}
