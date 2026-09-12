// <copyright file="DoubanPersonExternalId.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.ExternalId
{
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Providers;

    /// <inheritdoc />
    public class DoubanPersonExternalId : IExternalId
    {
        /// <inheritdoc />
        public string ProviderName => BaseProvider.DoubanProviderName;

        /// <inheritdoc />
        public string Key => BaseProvider.DoubanProviderId;

        /// <inheritdoc />
        public ExternalIdMediaType? Type => ExternalIdMediaType.Person;

        /// <inheritdoc />
        public bool Supports(IHasProviderIds item) => item is Person;
    }
}