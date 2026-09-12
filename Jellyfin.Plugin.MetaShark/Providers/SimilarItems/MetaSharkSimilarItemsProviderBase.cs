// <copyright file="MetaSharkSimilarItemsProviderBase.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.SimilarItems
{
#pragma warning disable CA1822 // Name/Type/CacheDuration 由子类用于实现 ISimilarItemsProvider 实例契约
    using System;
    using System.Collections.Generic;
    using System.Globalization;
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
    /// 相似项目提供商的共用实现：优先用豆瓣「喜欢这部电影/电视剧的人也喜欢」，
    /// 没有豆瓣编号或豆瓣没有结果时回退到 TMDb recommendations（受插件的「启用获取tmdb元数据」开关控制）。
    /// 远程提供商不会自动生效，需要在媒体库设置里把 MetaShark 勾选为「相似项目提供商」；
    /// 是否调用本提供商由宿主按库的 TypeOptions.SimilarItemProviders 过滤，因此这里只判断插件开关与条目 id。
    /// </summary>
    public abstract class MetaSharkSimilarItemsProviderBase
    {
        /// <summary>
        /// 展示在媒体库「相似项目提供商」列表里的名字，必须与库设置里勾选的值一致。
        /// </summary>
        public const string ProviderDisplayName = "MetaShark";

        private const string DoubanSource = "Douban";
        private const string TmdbSource = "Tmdb";
        private const float MaxRating = 10.0f;
        private const int MaxRecommendations = 20;

        private static readonly Action<ILogger, string, string, int, Exception?> LogSourceResolved =
            LoggerMessage.Define<string, string, int>(LogLevel.Debug, new EventId(1, "GetSimilarItemsAsync"), "[MetaShark] 相似项目解析完成. source={Source} id={Id} count={Count}.");

        private static readonly Action<ILogger, Guid, Exception?> LogProviderIdMissing =
            LoggerMessage.Define<Guid>(LogLevel.Debug, new EventId(2, "GetSimilarItemsAsync"), "[MetaShark] 跳过相似项目. reason=\"ProviderIdMissing\" itemId={ItemId}.");

        private static readonly Action<ILogger, Guid, string, Exception?> LogSourceRoute =
            LoggerMessage.Define<Guid, string>(LogLevel.Debug, new EventId(3, "GetSimilarItemsAsync"), "[MetaShark] 相似项目来源路由. itemId={ItemId} plan={Plan}.");

        private readonly DoubanApi doubanApi;
        private readonly TmdbApi tmdbApi;
        private readonly ILogger logger;

        protected MetaSharkSimilarItemsProviderBase(DoubanApi doubanApi, TmdbApi tmdbApi, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(doubanApi);
            ArgumentNullException.ThrowIfNull(tmdbApi);
            ArgumentNullException.ThrowIfNull(logger);

            this.doubanApi = doubanApi;
            this.tmdbApi = tmdbApi;
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
        /// 把 10 分制评分换算成 Jellyfin 的相似度分数（0-1）。没有评分时返回 null，表示不参与加权。
        /// </summary>
        internal static float? ToScore(float? rating)
        {
            if (rating is not > 0)
            {
                return null;
            }

            return Math.Clamp(rating.Value / MaxRating, 0f, 1f);
        }

        /// <summary>
        /// 把一条豆瓣推荐转成 Jellyfin 的相似项目引用，用豆瓣条目编号做 ProviderId。
        /// </summary>
        internal static SimilarItemReference? TryCreateDoubanReference(DoubanRecommendation? recommendation)
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
        /// 把一条 TMDb 推荐转成 Jellyfin 的相似项目引用，用 TMDb id 做 ProviderId。
        /// </summary>
        internal static SimilarItemReference? TryCreateTmdbReference(TmdbSimilarItem? recommendation)
        {
            if (recommendation == null
                || recommendation.Id <= 0
                || string.IsNullOrWhiteSpace(recommendation.Title))
            {
                return null;
            }

            return new SimilarItemReference
            {
                ProviderName = MetadataProvider.Tmdb.ToString(),
                ProviderId = recommendation.Id.ToString(CultureInfo.InvariantCulture),
                Score = ToScore((float)recommendation.VoteAverage),
            };
        }

        /// <summary>
        /// 生成相似项目引用：豆瓣优先，TMDb 兜底。
        /// </summary>
        /// <param name="item">源条目.</param>
        /// <param name="isSeries">是否剧集，决定豆瓣 rexxar 与 TMDb 的端点.</param>
        /// <param name="query">查询参数.</param>
        /// <param name="cancellationToken">取消令牌.</param>
        protected async IAsyncEnumerable<SimilarItemReference> GetSimilarItemsAsync(
            BaseItem item,
            bool isSeries,
            SimilarItemsQuery query,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(item);

            var configuration = MetaSharkPlugin.Instance?.Configuration;
            var limit = query?.Limit is > 0 ? Math.Min(query.Limit.Value, MaxRecommendations) : MaxRecommendations;
            var emitted = 0;

            // 与刮削一致地按 DefaultScraperMode 路由：tmdb-only 时不再走豆瓣，只保留 TMDb。
            var doubanAllowed = configuration?.EnableDoubanSimilarItems == true
                && DefaultScraperPolicy.IsDoubanAllowed(configuration, DefaultScraperSemantic.AutomaticRefresh);
            var tmdbAllowed = configuration?.EnableTmdb == true;
            var doubanPlan = doubanAllowed ? DoubanSource : "-";
            var tmdbPlan = tmdbAllowed ? TmdbSource : "-";
            LogSourceRoute(this.logger, item.Id, $"{doubanPlan}<{tmdbPlan}", null);

            if (doubanAllowed
                && item.TryGetProviderId(BaseProvider.DoubanProviderId, out var sid)
                && !string.IsNullOrWhiteSpace(sid))
            {
                var doubanRecommendations = await this.doubanApi.GetRecommendationsAsync(sid, isSeries, cancellationToken).ConfigureAwait(false);
                LogSourceResolved(this.logger, DoubanSource, sid, doubanRecommendations.Count, null);

                foreach (var recommendation in doubanRecommendations)
                {
                    var reference = TryCreateDoubanReference(recommendation);
                    if (reference == null)
                    {
                        continue;
                    }

                    emitted++;
                    yield return reference;
                    if (emitted >= limit)
                    {
                        yield break;
                    }
                }
            }

            // 豆瓣没有编号、没有结果或该来源被关闭时，用 TMDb recommendations 兜底。
            if (emitted == 0
                && tmdbAllowed
                && item.TryGetProviderId(MetadataProvider.Tmdb, out var tmdbIdValue)
                && int.TryParse(tmdbIdValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId))
            {
                var tmdbRecommendations = await this.tmdbApi.GetRecommendationsAsync(tmdbId, isSeries, cancellationToken).ConfigureAwait(false);
                LogSourceResolved(this.logger, TmdbSource, tmdbIdValue, tmdbRecommendations.Count, null);

                foreach (var recommendation in tmdbRecommendations)
                {
                    var reference = TryCreateTmdbReference(recommendation);
                    if (reference == null)
                    {
                        continue;
                    }

                    emitted++;
                    yield return reference;
                    if (emitted >= limit)
                    {
                        yield break;
                    }
                }
            }

            if (emitted == 0)
            {
                LogProviderIdMissing(this.logger, item.Id, null);
            }
        }
    }
}
