// <copyright file="MetaSharkSeriesSimilarItemsProvider.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.SimilarItems
{
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Api;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// 剧集相似项目提供商（豆瓣优先，TMDb 兜底）。
    /// </summary>
    public sealed class MetaSharkSeriesSimilarItemsProvider : MetaSharkSimilarItemsProviderBase, IRemoteSimilarItemsProvider<Series>
    {
        public MetaSharkSeriesSimilarItemsProvider(DoubanApi doubanApi, TmdbApi tmdbApi, ILogger<MetaSharkSeriesSimilarItemsProvider> logger)
            : base(doubanApi, tmdbApi, logger)
        {
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<SimilarItemReference> GetSimilarItemsAsync(
            Series item,
            SimilarItemsQuery query,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var reference in this.GetSimilarItemsAsync(item, true, query, cancellationToken).ConfigureAwait(false))
            {
                yield return reference;
            }
        }
    }
}
