// <copyright file="DoubanExternalUrlProvider.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.ExternalId
{
    using System.Collections.Generic;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.Movies;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;

    /// <summary>
    /// External URLs for Douban.
    /// </summary>
    /// <remarks>
    /// 必须是 public：Jellyfin 通过程序集的 GetExportedTypes() 发现插件类型，internal 类型不会被实例化。
    /// </remarks>
    public sealed class DoubanExternalUrlProvider : IExternalUrlProvider
    {
        /// <inheritdoc/>
        public string Name => BaseProvider.DoubanProviderName;

        /// <inheritdoc/>
        public IEnumerable<string> GetExternalUrls(BaseItem item)
        {
            switch (item)
            {
                case Person:
                    if (item.TryGetProviderId(BaseProvider.DoubanProviderId, out var externalId))
                    {
                        yield return $"https://www.douban.com/personage/{externalId}/";
                    }

                    break;
                default:
                    if (item.TryGetProviderId(BaseProvider.DoubanProviderId, out externalId))
                    {
                        yield return $"https://movie.douban.com/subject/{externalId}/";
                    }

                    break;
            }
        }
    }
}