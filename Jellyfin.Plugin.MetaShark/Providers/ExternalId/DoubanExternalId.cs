// <copyright file="DoubanExternalId.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.ExternalId
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using MediaBrowser.Controller.Entities.Movies;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Providers;

    public class DoubanExternalId : IExternalId
    {
        public string ProviderName => BaseProvider.DoubanProviderName;

        public string Key => BaseProvider.DoubanProviderId;

        public ExternalIdMediaType? Type => null;

        public bool Supports(IHasProviderIds item)
        {
            return item is Movie || item is Series || item is Season;
        }
    }
}
