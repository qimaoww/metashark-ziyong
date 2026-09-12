// <copyright file="MetaSharkMovieSimilarItemsProvider.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.SimilarItems
{
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Api;
    using MediaBrowser.Controller.Entities.Movies;
    using MediaBrowser.Controller.Library;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// 电影相似项目提供商（豆瓣）。
    /// </summary>
    public sealed class MetaSharkMovieSimilarItemsProvider : MetaSharkSimilarItemsProviderBase, IRemoteSimilarItemsProvider<Movie>
    {
        public MetaSharkMovieSimilarItemsProvider(DoubanApi doubanApi, ILogger<MetaSharkMovieSimilarItemsProvider> logger)
            : base(doubanApi, logger)
        {
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<SimilarItemReference> GetSimilarItemsAsync(
            Movie item,
            SimilarItemsQuery query,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var reference in this.GetDoubanSimilarItemsAsync(item, false, query, cancellationToken).ConfigureAwait(false))
            {
                yield return reference;
            }
        }
    }
}
