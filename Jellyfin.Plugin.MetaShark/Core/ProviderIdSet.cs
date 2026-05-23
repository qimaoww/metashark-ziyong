// <copyright file="ProviderIdSet.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Core
{
    using System.Collections.Generic;
    using System.Globalization;
    using Jellyfin.Plugin.MetaShark.Model;
    using Jellyfin.Plugin.MetaShark.Providers;
    using MediaBrowser.Model.Entities;

    internal static class ProviderIdSet
    {
        public static Dictionary<string, string> ForDouban(string sid)
        {
            return new Dictionary<string, string>
            {
                { BaseProvider.DoubanProviderId, sid },
                { MetaSharkPlugin.ProviderId, $"{MetaSource.Douban}_{sid}" },
            };
        }

        public static Dictionary<string, string> ForTmdb(int tmdbId)
        {
            return new Dictionary<string, string>
            {
                { MetadataProvider.Tmdb.ToString(), tmdbId.ToString(CultureInfo.InvariantCulture) },
                { MetaSharkPlugin.ProviderId, $"{MetaSource.Tmdb}_{tmdbId}" },
            };
        }
    }
}
