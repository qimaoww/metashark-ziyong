// <copyright file="MetaSharkSimilarItemsProviderBase.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.SimilarItems
{
#pragma warning disable CA1822 // Name/Type/CacheDuration 由子类用于实现 ISimilarItemsProvider 实例契约
    using System;
    using System.Collections.Generic;
    using System.Runtime.CompilerServices;
    using System.Threading;
    using Jellyfin.Plugin.MetaShark.Api;
    using Jellyfin.Plugin.MetaShark.Model;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Model.Configuration;
    using MediaBrowser.Model.Entities;
    using Microsoft.Extensions.Logging;

    /// <summary>
    /// 豆瓣相似项目提供商的共用实现：把豆瓣的「喜欢这部电影/电视剧的人也喜欢」转成 Jellyfin 的相似项目引用。
    /// 远程提供商不会自动生效，需要在媒体库设置里把 MetaShark 勾选为「相似项目提供商」；
    /// 是否调用本提供商由宿主按库的 TypeOptions.SimilarItemProviders 过滤，因此这里只判断插件总开关与豆瓣编号。
    /// </summary>
    public abstract class MetaSharkSimilarItemsProviderBase
    {
        /// <summary>
        /// 展示在媒体库「相似项目提供商」列表里的名字，必须与库设置里勾选的值一致。
        /// </summary>
        public const string ProviderDisplayName = "MetaShark";

        private const float DoubanMaxRating = 10.0f;
        private const int MaxRecommendations = 20;

        private static readonly Action<ILogger, Guid, Exception?> LogDoubanIdMissing =
            LoggerMessage.Define<Guid>(LogLevel.Debug, new EventId(1, "GetSimilarItemsAsync"), "[MetaShark] 跳过豆瓣相似项目. reason=\"DoubanIdMissing\" itemId={ItemId}.");

        private static readonly Action<ILogger, string, int, Exception?> LogRecommendationsResolved =
            LoggerMessage.Define<string, int>(LogLevel.Debug, new EventId(2, "GetSimilarItemsAsync"), "[MetaShark] 豆瓣相似项目解析完成. sid={Sid} count={Count}.");

        private readonly DoubanApi doubanApi;
        private readonly ILogger logger;

        protected MetaSharkSimilarItemsProviderBase(DoubanApi doubanApi, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(doubanApi);
            ArgumentNullException.ThrowIfNull(logger);

            this.doubanApi = doubanApi;
            this.logger = logger;
        }

        /// <summary>
        /// Gets 展示名称。
        /// </summary>
        public string Name => ProviderDisplayName;

        /// <summary>
        /// Gets 提供商类型。
        /// </summary>
        public MetadataPluginType Type => MetadataPluginType.SimilarityProvider;

        /// <summary>
        /// Gets 结果缓存时长，由配置的缓存天数决定。
        /// </summary>
        public TimeSpan? CacheDuration
        {
            get
            {
                var days = MetaSharkPlugin.Instance?.Configuration.DoubanSimilarItemsCacheDays ?? 0;
                return days > 0 ? TimeSpan.FromDays(days) : null;
            }
        }

        /// <summary>
        /// 把豆瓣评分换算成 Jellyfin 的相似度分数（0-1）。豆瓣没有评分时返回 null，表示不参与加权。
        /// </summary>
        internal static float? ToScore(float? doubanRating)
        {
            if (doubanRating is not > 0)
            {
                return null;
            }

            return Math.Clamp(doubanRating.Value / DoubanMaxRating, 0f, 1f);
        }

        /// <summary>
        /// 把一条豆瓣推荐转成 Jellyfin 的相似项目引用，用豆瓣条目编号做 ProviderId。
        /// </summary>
        internal static SimilarItemReference? TryCreateReference(DoubanRecommendation? recommendation)
        {
            if (recommendation == null
                || string.IsNullOrWhiteSpace(recommendation.Id)
                || string.IsNullOrWhiteSpace(recommendation.Title))
            {
                return null;
            }

            return new SimilarItemReference
            {
                ProviderName = BaseProvider.DoubanProviderId,
                ProviderId = recommendation.Id,
                Score = ToScore(recommendation.Rating?.Value),
            };
        }

        /// <summary>
        /// 生成相似项目引用。
        /// </summary>
        /// <param name="item">源条目.</param>
        /// <param name="isSeries">是否剧集，决定 rexxar 的 tv/movie 端点.</param>
        /// <param name="query">查询参数.</param>
        /// <param name="cancellationToken">取消令牌.</param>
        protected async IAsyncEnumerable<SimilarItemReference> GetDoubanSimilarItemsAsync(
            BaseItem item,
            bool isSeries,
            SimilarItemsQuery query,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);

            if (!(MetaSharkPlugin.Instance?.Configuration.EnableDoubanSimilarItems ?? false))
            {
                yield break;
            }

            if (!item.TryGetProviderId(BaseProvider.DoubanProviderId, out var sid) || string.IsNullOrWhiteSpace(sid))
            {
                LogDoubanIdMissing(this.logger, item.Id, null);
                yield break;
            }

            var recommendations = await this.doubanApi.GetRecommendationsAsync(sid, isSeries, cancellationToken).ConfigureAwait(false);
            LogRecommendationsResolved(this.logger, sid, recommendations.Count, null);

            var limit = query?.Limit is > 0 ? Math.Min(query.Limit.Value, MaxRecommendations) : MaxRecommendations;
            var emitted = 0;
            foreach (var recommendation in recommendations)
            {
                if (emitted >= limit)
                {
                    yield break;
                }

                var reference = TryCreateReference(recommendation);
                if (reference == null)
                {
                    continue;
                }

                emitted++;
                yield return reference;
            }
        }
    }
}
