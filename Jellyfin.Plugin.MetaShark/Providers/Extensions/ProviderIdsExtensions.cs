// <copyright file="ProviderIdsExtensions.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers
{
    using System;
    using System.Collections.Generic;
    using Jellyfin.Plugin.MetaShark.Model;
    using MediaBrowser.Model.Entities;

    public static class ProviderIdsExtensions
    {
        private const string MetaSharkTmdbPrefix = "Tmdb_";

        public static MetaSource GetMetaSource(this IHasProviderIds instance, string name)
        {
            var value = instance.GetProviderId(name);
            return value.ToMetaSource();
        }

        public static string? GetTmdbId(this IHasProviderIds instance)
        {
            ArgumentNullException.ThrowIfNull(instance);

            return instance.TryGetTmdbId(out var tmdbId) ? tmdbId : null;
        }

        public static bool TryGetTmdbId(this IHasProviderIds instance, out string tmdbId)
        {
            ArgumentNullException.ThrowIfNull(instance);

            if (TryNormalizeTmdbId(instance.GetProviderId(BaseProvider.MetaSharkTmdbProviderId), out tmdbId))
            {
                return true;
            }

            if (TryNormalizeTmdbId(instance.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb), out tmdbId))
            {
                return true;
            }

            return TryReadTmdbIdFromMetaSharkProviderId(instance.GetProviderId(MetaSharkPlugin.ProviderId), out tmdbId);
        }

        public static bool TryGetTmdbId(this IReadOnlyDictionary<string, string>? providerIds, out string tmdbId)
        {
            tmdbId = string.Empty;
            if (providerIds == null)
            {
                return false;
            }

            if (providerIds.TryGetValue(BaseProvider.MetaSharkTmdbProviderId, out var privateTmdbId)
                && TryNormalizeTmdbId(privateTmdbId, out tmdbId))
            {
                return true;
            }

            if (providerIds.TryGetValue(MediaBrowser.Model.Entities.MetadataProvider.Tmdb.ToString(), out var officialTmdbId)
                && TryNormalizeTmdbId(officialTmdbId, out tmdbId))
            {
                return true;
            }

            return providerIds.TryGetValue(MetaSharkPlugin.ProviderId, out var metaSharkId)
                && TryReadTmdbIdFromMetaSharkProviderId(metaSharkId, out tmdbId);
        }

        public static void TryGetMetaSource(this Dictionary<string, string> dict, string name, out MetaSource metaSource)
        {
            ArgumentNullException.ThrowIfNull(dict);
            if (dict.TryGetValue(name, out var value))
            {
                metaSource = value.ToMetaSource();
            }
            else
            {
                metaSource = MetaSource.None;
            }
        }

        private static bool TryReadTmdbIdFromMetaSharkProviderId(string? providerId, out string tmdbId)
        {
            tmdbId = string.Empty;
            if (string.IsNullOrWhiteSpace(providerId)
                || !providerId.StartsWith(MetaSharkTmdbPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return TryNormalizeTmdbId(providerId[MetaSharkTmdbPrefix.Length..], out tmdbId);
        }

        private static bool TryNormalizeTmdbId(string? providerId, out string tmdbId)
        {
            tmdbId = string.Empty;
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return false;
            }

            tmdbId = providerId.Trim();
            return true;
        }
    }
}
