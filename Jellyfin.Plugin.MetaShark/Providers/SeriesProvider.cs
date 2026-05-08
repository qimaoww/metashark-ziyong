// <copyright file="SeriesProvider.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Data.Enums;
    using Jellyfin.Plugin.MetaShark.Api;
    using Jellyfin.Plugin.MetaShark.Core;
    using Jellyfin.Plugin.MetaShark.EpisodeGroupMapping;
    using Jellyfin.Plugin.MetaShark.Model;
    using Jellyfin.Plugin.MetaShark.Providers.Llm;
    using Jellyfin.Plugin.MetaShark.Workers;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Providers;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;
    using TMDbLib.Objects.Search;
    using TMDbLib.Objects.TvShows;
    using MetadataProvider = MediaBrowser.Model.Entities.MetadataProvider;

    [SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1204:Static elements should appear before non-static members", Justification = "Keep provider orchestration helpers near the flow they support.")]
    public class SeriesProvider : BaseProvider, IRemoteMetadataProvider<Series, SeriesInfo>
    {
        private readonly ILlmMetadataAssistService? llmMetadataAssistService;
        private readonly ILlmExternalIdResolutionService? llmExternalIdResolutionService;
        private readonly ILlmEpisodeGroupMappingProviderAssistService? llmEpisodeGroupMappingProviderAssistService;
        private readonly IMovieSeriesPeopleOverwriteRefreshCandidateStore? movieSeriesPeopleOverwriteRefreshCandidateStore;
        private readonly LlmTmdbIdCorrectionTriggerPolicy llmTmdbIdCorrectionTriggerPolicy = new LlmTmdbIdCorrectionTriggerPolicy();

        public SeriesProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, DoubanApi doubanApi, TmdbApi tmdbApi, OmdbApi omdbApi, ImdbApi imdbApi, IMovieSeriesPeopleOverwriteRefreshCandidateStore? movieSeriesPeopleOverwriteRefreshCandidateStore = null, ILlmMetadataAssistService? llmMetadataAssistService = null, ILlmEpisodeGroupMappingProviderAssistService? llmEpisodeGroupMappingProviderAssistService = null, ILlmExternalIdResolutionService? llmExternalIdResolutionService = null)
            : base(httpClientFactory, loggerFactory.CreateLogger<SeriesProvider>(), libraryManager, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi)
        {
            this.movieSeriesPeopleOverwriteRefreshCandidateStore = movieSeriesPeopleOverwriteRefreshCandidateStore ?? InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore.Shared;
            this.llmMetadataAssistService = llmMetadataAssistService;
            this.llmExternalIdResolutionService = llmExternalIdResolutionService;
            this.llmEpisodeGroupMappingProviderAssistService = llmEpisodeGroupMappingProviderAssistService;
        }

        public string Name => MetaSharkPlugin.PluginName;

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(searchInfo);
            this.Log("开始搜索剧集候选. name: {0}", searchInfo.Name);
            var result = new List<RemoteSearchResult>();
            var hasExactTmdbHit = false;
            var hasUsableTitle = !string.IsNullOrWhiteSpace(searchInfo.Name);

            if (Config.EnableTmdb)
            {
                var tmdbIdStr = searchInfo.GetProviderId(MetadataProvider.Tmdb);
                var formattedTmdbId = FormatProviderIdForLog(tmdbIdStr);
                if (string.IsNullOrWhiteSpace(tmdbIdStr))
                {
                    this.Log("跳过显式 TMDb ID 匹配，provider id 为空. rawProviderId: {0} fallbackToTitleSearch: {1}", formattedTmdbId, hasUsableTitle);
                }
                else if (!int.TryParse(tmdbIdStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId))
                {
                    this.Log("跳过显式 TMDb ID 匹配，provider id 无效. rawProviderId: {0} fallbackToTitleSearch: {1}", formattedTmdbId, hasUsableTitle);
                }
                else if (tmdbId <= 0)
                {
                    this.Log("跳过显式 TMDb ID 匹配，provider id 小于等于 0. rawProviderId: {0} fallbackToTitleSearch: {1}", formattedTmdbId, hasUsableTitle);
                }
                else
                {
                    this.Log("尝试显式 TMDb ID 精确匹配. tmdbId: {0}", tmdbId);
                    var tvShow = await this.TmdbApi.GetSeriesAsync(tmdbId, searchInfo.MetadataLanguage, searchInfo.MetadataLanguage, cancellationToken).ConfigureAwait(false);
                    if (tvShow != null)
                    {
                        result.Add(this.MapTmdbSeriesSearchResult(tvShow));
                        hasExactTmdbHit = true;
                    }
                    else
                    {
                        this.Log("显式 TMDb ID 未命中剧集，回退标题搜索. rawProviderId: {0} fallbackToTitleSearch: {1}", formattedTmdbId, hasUsableTitle);
                    }
                }
            }

            if (!hasUsableTitle)
            {
                return result;
            }

            // 从douban搜索
            var res = await this.DoubanApi.SearchTVAsync(searchInfo.Name, cancellationToken).ConfigureAwait(false);
            result.AddRange(res.Take(Configuration.PluginConfiguration.MAXSEARCHRESULT).Select(x =>
            {
                return new RemoteSearchResult
                {
                    // 这里 MetaSharkPlugin.ProviderId 的值做这么复杂，是为了和电影保持一致并唯一
                    ProviderIds = new Dictionary<string, string> { { DoubanProviderId, x.Sid }, { MetaSharkPlugin.ProviderId, $"{MetaSource.Douban}_{x.Sid}" } },
                    ImageUrl = this.GetProxyImageUrl(new Uri(x.Img, UriKind.Absolute)).ToString(),
                    ProductionYear = x.Year,
                    Name = x.Name,
                };
            }));

            // 尝试从tmdb搜索
            if (Config.EnableTmdbSearch)
            {
                if (hasExactTmdbHit)
                {
                    this.Log("已命中显式 TMDb ID，跳过 TMDb 标题搜索");
                }
                else
                {
                    var tmdbList = await this.TmdbApi.SearchSeriesAsync(searchInfo.Name, searchInfo.MetadataLanguage, cancellationToken).ConfigureAwait(false);
                    result.AddRange(tmdbList.Take(Configuration.PluginConfiguration.MAXSEARCHRESULT).Select(x => this.MapTmdbSeriesSearchResult(x)));
                }
            }

            return result;
        }

        /// <inheritdoc />
        public async Task<MetadataResult<Series>> GetMetadata(SeriesInfo info, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(info);
            if (string.IsNullOrEmpty(info.MetadataLanguage) && !string.IsNullOrEmpty(info.Name) && info.Name.HasChinese())
            {
                info.MetadataLanguage = "zh-CN";
            }

            var fileName = GetOriginalFileName(info);
            var result = new MetadataResult<Series>();
            var semantic = this.ResolveMetadataSemantic(info);
            var doubanAllowed = IsDoubanAllowed(semantic);

            var sid = info.GetProviderId(DoubanProviderId);
            var tmdbId = info.GetProviderId(MetadataProvider.Tmdb);
            var originalTmdbId = tmdbId;
            var originalPublicProviderIds = CreatePublicProviderIdCopy(info.ProviderIds);
            var hasVerifiedTmdbCorrection = false;
            var metaSource = info.GetMetaSource(MetaSharkPlugin.ProviderId);
            if (metaSource == MetaSource.Tmdb && string.IsNullOrWhiteSpace(tmdbId))
            {
                metaSource = MetaSource.None;
            }

            var effectiveSid = doubanAllowed ? sid : null;

            var tmdbSourceIsPrimary = metaSource == MetaSource.Tmdb && (!doubanAllowed || semantic == DefaultScraperSemantic.ManualMatch);

            // 注意：会存在元数据有tmdbId，但metaSource没值的情况（之前由TMDB插件刮削导致）
            var hasTmdbMeta = !string.IsNullOrEmpty(tmdbId) && (!doubanAllowed || tmdbSourceIsPrimary);
            var hasDoubanMeta = !tmdbSourceIsPrimary && !string.IsNullOrEmpty(effectiveSid);
            var llmExternalIdResolutionResult = LlmExternalIdResolutionResult.NotTriggered("TmdbProviderIdPresent");
            var tmdbIdResolvedByLlmExternalIds = false;
            var tmdbCorrectionResult = await this.TryResolveSeriesTmdbCorrectionAsync(info, semantic, originalTmdbId, cancellationToken).ConfigureAwait(false);
            if (tmdbCorrectionResult.ShouldReplace && !string.IsNullOrWhiteSpace(tmdbCorrectionResult.ReplacementTmdbId))
            {
                tmdbId = tmdbCorrectionResult.ReplacementTmdbId;
                hasVerifiedTmdbCorrection = true;
            }

            if (string.IsNullOrWhiteSpace(tmdbId))
            {
                llmExternalIdResolutionResult = await this.TryResolveSeriesExternalIdsWithLlmAsync(info, semantic, cancellationToken).ConfigureAwait(false);
                ApplyLlmExternalIdWrites(info, llmExternalIdResolutionResult);
                var resolvedTmdbId = GetSeriesTmdbIdFromLlmExternalIdWrites(llmExternalIdResolutionResult);
                if (!string.IsNullOrWhiteSpace(resolvedTmdbId))
                {
                    tmdbId = resolvedTmdbId;
                    tmdbIdResolvedByLlmExternalIds = true;
                }
            }

            var llmAssistResult = LlmScrapingAssistResult.NotTriggered("AuthoritativeMetadataPresent");
            if (!hasDoubanMeta && !hasTmdbMeta)
            {
                llmAssistResult = await this.TryAssistSeriesMetadataWithLlmAsync(info, semantic, cancellationToken).ConfigureAwait(false);
            }

            this.Log("开始获取剧集元数据. name: {0} fileName: {1} metaSource: {2} enableTmdb: {3}", info.Name, fileName, metaSource, Config.EnableTmdb);
            if (!hasDoubanMeta && !hasTmdbMeta)
            {
                var llmSearchHints = llmAssistResult.SearchHints;
                var preferLlmSearchHints = ShouldPreferLlmSeriesSearchHints(info, fileName, llmSearchHints);

                // 自动扫描搜索匹配元数据
                if (doubanAllowed)
                {
                    if (preferLlmSearchHints)
                    {
                        sid = await this.GuessByDoubanWithLlmHintsAsync(llmSearchHints, info, cancellationToken).ConfigureAwait(false);
                    }

                    if (string.IsNullOrEmpty(sid))
                    {
                        sid = await this.GuessByDoubanAsync(info, cancellationToken).ConfigureAwait(false);
                    }

                    if (string.IsNullOrEmpty(sid) && !preferLlmSearchHints)
                    {
                        sid = await this.GuessByDoubanWithLlmHintsAsync(llmSearchHints, info, cancellationToken).ConfigureAwait(false);
                    }

                    effectiveSid = sid;
                }

                if (string.IsNullOrEmpty(effectiveSid) && string.IsNullOrEmpty(tmdbId) && Config.EnableTmdbMatch)
                {
                    if (preferLlmSearchHints)
                    {
                        tmdbId = await this.GuessByTmdbWithLlmHintsAsync(llmSearchHints, info, cancellationToken).ConfigureAwait(false);
                    }

                    if (string.IsNullOrEmpty(tmdbId))
                    {
                        tmdbId = await this.GuestByTmdbAsync(info, cancellationToken).ConfigureAwait(false);
                    }

                    if (string.IsNullOrEmpty(tmdbId) && !preferLlmSearchHints)
                    {
                        tmdbId = await this.GuessByTmdbWithLlmHintsAsync(llmSearchHints, info, cancellationToken).ConfigureAwait(false);
                    }

                    if (!string.IsNullOrEmpty(tmdbId))
                    {
                        metaSource = MetaSource.Tmdb;
                    }
                }
            }

            if (!tmdbSourceIsPrimary && !string.IsNullOrEmpty(effectiveSid))
            {
                this.Log("通过 Douban 获取剧集元数据. sid: {0}", effectiveSid);
                var subject = await this.DoubanApi.GetMovieAsync(effectiveSid, cancellationToken).ConfigureAwait(false);
                if (subject == null)
                {
                    if (string.IsNullOrEmpty(tmdbId) && Config.EnableTmdbMatch)
                    {
                        tmdbId = await this.GuestByTmdbAsync(info, cancellationToken).ConfigureAwait(false);
                    }

                    if (!string.IsNullOrEmpty(tmdbId))
                    {
                        var tmdbFallbackResult = await this.GetMetadataByTmdb(tmdbId, info, cancellationToken).ConfigureAwait(false);
                        ApplyLlmExternalIdWrites(tmdbFallbackResult, llmExternalIdResolutionResult);
                        ApplyLlmTextCompletion(tmdbFallbackResult, llmAssistResult);
                        return FinalizeMetadataResult(tmdbFallbackResult, originalTmdbId, originalPublicProviderIds, hasVerifiedTmdbCorrection);
                    }

                    return FinalizeMetadataResult(result, originalTmdbId, originalPublicProviderIds, hasVerifiedTmdbCorrection);
                }

                var seriesName = this.RemoveSeasonSuffix(subject.Name);
                var item = new Series
                {
                    ProviderIds = new Dictionary<string, string> { { DoubanProviderId, subject.Sid }, { MetaSharkPlugin.ProviderId, $"{MetaSource.Douban}_{subject.Sid}" } },
                    Name = seriesName,
                    OriginalTitle = this.RemoveSeasonSuffix(subject.OriginalName),
                    CommunityRating = subject.Rating,
                    Overview = subject.Intro,
                    ProductionYear = subject.Year,
                    HomePageUrl = "https://www.douban.com",
                    Genres = subject.Genres.ToArray(),
                    PremiereDate = subject.ScreenTime,
                    Tagline = string.Empty,
                };

                if (!string.IsNullOrEmpty(tmdbId))
                {
                    item.SetProviderId(MetadataProvider.Tmdb, tmdbId);
                }

                // 设置imdb元数据
                if (!string.IsNullOrEmpty(subject.Imdb))
                {
                    var newImdbId = await this.CheckNewImdbID(subject.Imdb, cancellationToken).ConfigureAwait(false);
                    subject.Imdb = newImdbId;
                    item.SetProviderId(MetadataProvider.Imdb, newImdbId);
                }

                // 搜索匹配tmdbId
                if (string.IsNullOrEmpty(tmdbId) && !tmdbIdResolvedByLlmExternalIds)
                {
                    var newTmdbId = await this.FindTmdbId(seriesName, subject.Imdb, subject.Year, info, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(newTmdbId))
                    {
                        tmdbId = newTmdbId;
                        item.SetProviderId(MetadataProvider.Tmdb, tmdbId);
                    }
                }

                if (!string.IsNullOrEmpty(tmdbId))
                {
                    await this.TryPopulateTvExternalIdsFromTmdbAsync(item, tmdbId, info, cancellationToken).ConfigureAwait(false);
                    await this.TryAssistEpisodeGroupMappingWithLlmAsync(tmdbId, item.Name, info, semantic, cancellationToken).ConfigureAwait(false);
                }

                // 通过imdb获取电影分级信息
                if (Config.EnableTmdbOfficialRating && !string.IsNullOrEmpty(tmdbId))
                {
                    var officialRating = await this.GetTmdbOfficialRating(info, tmdbId, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(officialRating))
                    {
                        item.OfficialRating = officialRating;
                    }
                }

                ApplyLlmExternalIdWrites(item, llmExternalIdResolutionResult);

                result.Item = item;
                result.QueriedById = true;
                result.HasMetadata = true;

                if (!string.IsNullOrEmpty(tmdbId))
                {
                    var acceptedPeopleCount = await this.TryAddTmdbPeopleAsync(tmdbId, info, result, cancellationToken).ConfigureAwait(false);
                    this.TryQueueSearchMissingMetadataOverwriteCandidate(info, tmdbId, result.People, acceptedPeopleCount);
                }

                ApplyLlmTextCompletion(result, llmAssistResult);

                return FinalizeMetadataResult(result, originalTmdbId, originalPublicProviderIds, hasVerifiedTmdbCorrection);
            }

            if (!string.IsNullOrEmpty(tmdbId) && (!doubanAllowed || tmdbSourceIsPrimary || string.IsNullOrEmpty(effectiveSid)))
            {
                var tmdbResult = await this.GetMetadataByTmdb(tmdbId, info, cancellationToken).ConfigureAwait(false);
                if (tmdbResult.HasMetadata)
                {
                    await this.TryAssistEpisodeGroupMappingWithLlmAsync(tmdbId, tmdbResult.Item?.Name, info, semantic, cancellationToken).ConfigureAwait(false);
                }

                ApplyLlmExternalIdWrites(tmdbResult, llmExternalIdResolutionResult);
                ApplyLlmTextCompletion(tmdbResult, llmAssistResult);
                return FinalizeMetadataResult(tmdbResult, originalTmdbId, originalPublicProviderIds, hasVerifiedTmdbCorrection);
            }

            this.Log("剧集匹配失败，可检查年份是否与豆瓣一致，或是否需要登录访问. name: {0} year: {1}", info.Name, info.Year);
            return FinalizeMetadataResult(result, originalTmdbId, originalPublicProviderIds, hasVerifiedTmdbCorrection);
        }

        private static MetadataResult<Series> FinalizeMetadataResult(MetadataResult<Series> result, string? originalTmdbId, IReadOnlyDictionary<string, string>? originalPublicProviderIds, bool hasVerifiedCorrection)
        {
            TmdbProviderIdPreservationHelper.PreserveSeriesTmdbId(originalTmdbId, result.Item, hasVerifiedCorrection);
            PreserveNonTmdbProviderIdsAfterCorrection(result.Item, originalPublicProviderIds, hasVerifiedCorrection);
            return result;
        }

        private static void PreserveNonTmdbProviderIdsAfterCorrection(Series? series, IReadOnlyDictionary<string, string>? originalPublicProviderIds, bool hasVerifiedCorrection)
        {
            if (!hasVerifiedCorrection || series == null || originalPublicProviderIds == null)
            {
                return;
            }

            PreserveProviderId(series, originalPublicProviderIds, MetadataProvider.Imdb.ToString());
            PreserveProviderId(series, originalPublicProviderIds, MetadataProvider.Tvdb.ToString());
            PreserveProviderId(series, originalPublicProviderIds, DoubanProviderId);
        }

        private static void PreserveProviderId(Series series, IReadOnlyDictionary<string, string> originalProviderIds, string providerIdKey)
        {
            if (originalProviderIds.TryGetValue(providerIdKey, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                series.SetProviderId(providerIdKey, value);
            }
        }

        private static bool HasCompleteLlmConfiguration(Configuration.PluginConfiguration configuration)
        {
            return configuration.EnableLlmAssist
                && !string.IsNullOrWhiteSpace(configuration.LlmBaseUrl)
                && !string.IsNullOrWhiteSpace(configuration.LlmModel)
                && !string.IsNullOrWhiteSpace(configuration.LlmApiKey);
        }

        private static bool ShouldPreferLlmSeriesSearchHints(SeriesInfo info, string fileName, LlmSearchHints searchHints)
        {
            if (!searchHints.HasHints)
            {
                return false;
            }

            if (searchHints.Year.HasValue && info.Year.HasValue && Math.Abs(searchHints.Year.Value - info.Year.Value) > 1)
            {
                return true;
            }

            var hintTitle = NormalizeLlmHintText(searchHints.Title);
            if (hintTitle == null)
            {
                return false;
            }

            return IsDifferentNonEmptyText(hintTitle, info.Name) && IsDifferentNonEmptyText(hintTitle, fileName);
        }

        private static bool IsDifferentNonEmptyText(string value, string? other)
        {
            var normalizedOther = NormalizeLlmHintText(other);
            return normalizedOther != null && !string.Equals(value, normalizedOther, StringComparison.OrdinalIgnoreCase);
        }

        private static string? NormalizeLlmHintText(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static SeriesInfo CreateSafeLlmSeriesLookupInfo(SeriesInfo info)
        {
            var safePath = Config.LlmAllowRelativePathContext
                ? LlmRelativePathSanitizer.Sanitize(info.Path, Array.Empty<string?>(), "Series")
                : string.Empty;

            return new SeriesInfo
            {
                Name = info.Name,
                Path = safePath,
                MetadataLanguage = info.MetadataLanguage,
                MetadataCountryCode = info.MetadataCountryCode,
                Year = info.Year,
                ParentIndexNumber = info.ParentIndexNumber,
                IndexNumber = info.IndexNumber,
                IsAutomated = info.IsAutomated,
                ProviderIds = CreateProviderIdPresenceOnlyCopy(info.ProviderIds),
            };
        }

        private static SeriesInfo CreateSafeLlmExternalIdSeriesLookupInfo(SeriesInfo info)
        {
            var safePath = Config.LlmAllowRelativePathContext
                ? LlmRelativePathSanitizer.Sanitize(info.Path, Array.Empty<string?>(), nameof(Series))
                : string.Empty;

            return new SeriesInfo
            {
                Name = info.Name,
                Path = safePath,
                MetadataLanguage = info.MetadataLanguage,
                MetadataCountryCode = info.MetadataCountryCode,
                Year = info.Year,
                ParentIndexNumber = info.ParentIndexNumber,
                IndexNumber = info.IndexNumber,
                IsAutomated = info.IsAutomated,
                ProviderIds = CreatePublicProviderIdCopy(info.ProviderIds),
            };
        }

        private static SeriesInfo CreateSafeLlmTmdbCorrectionSeriesLookupInfo(SeriesInfo info)
        {
            var safePath = Config.LlmAllowRelativePathContext
                ? LlmRelativePathSanitizer.Sanitize(info.Path, Array.Empty<string?>(), nameof(Series))
                : string.Empty;

            return new SeriesInfo
            {
                Name = info.Name,
                Path = safePath,
                MetadataLanguage = info.MetadataLanguage,
                MetadataCountryCode = info.MetadataCountryCode,
                Year = info.Year,
                ParentIndexNumber = info.ParentIndexNumber,
                IndexNumber = info.IndexNumber,
                IsAutomated = info.IsAutomated,
                ProviderIds = CreatePublicProviderIdCopy(info.ProviderIds),
            };
        }

        private static Dictionary<string, string>? CreateProviderIdPresenceOnlyCopy(Dictionary<string, string>? providerIds)
        {
            return providerIds?.Keys.ToDictionary(key => key, _ => "present", StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, string>? CreatePublicProviderIdCopy(Dictionary<string, string>? providerIds)
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

            if (string.Equals(key, DoubanProviderId, StringComparison.OrdinalIgnoreCase))
            {
                normalizedKey = DoubanProviderId;
                return true;
            }

            normalizedKey = string.Empty;
            return false;
        }

        private static string? GetSeriesTmdbIdFromLlmExternalIdWrites(LlmExternalIdResolutionResult resolutionResult)
        {
            return resolutionResult.ProviderIdWrites
                .FirstOrDefault(write => string.Equals(write.ProviderIdKey, MetadataProvider.Tmdb.ToString(), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(write.MediaType, nameof(Series), StringComparison.Ordinal))
                ?.ProviderIdValue;
        }

        private static void ApplyLlmExternalIdWrites(ItemLookupInfo info, LlmExternalIdResolutionResult resolutionResult)
        {
            if (resolutionResult.ProviderIdWrites.Count == 0)
            {
                return;
            }

            var providerIds = info.ProviderIds ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var write in resolutionResult.ProviderIdWrites)
            {
                if (!string.Equals(write.MediaType, nameof(Series), StringComparison.Ordinal))
                {
                    continue;
                }

                if (providerIds.TryGetValue(write.ProviderIdKey, out var existingValue) && !string.IsNullOrWhiteSpace(existingValue))
                {
                    continue;
                }

                providerIds[write.ProviderIdKey] = write.ProviderIdValue;
            }
        }

        private static void ApplyLlmExternalIdWrites(MetadataResult<Series> result, LlmExternalIdResolutionResult resolutionResult)
        {
            if (result.Item != null && result.HasMetadata)
            {
                ApplyLlmExternalIdWrites(result.Item, resolutionResult);
            }
        }

        private static void ApplyLlmExternalIdWrites(Series series, LlmExternalIdResolutionResult resolutionResult)
        {
            foreach (var write in resolutionResult.ProviderIdWrites)
            {
                if (!string.Equals(write.MediaType, nameof(Series), StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(series.GetProviderId(write.ProviderIdKey)))
                {
                    series.SetProviderId(write.ProviderIdKey, write.ProviderIdValue);
                }
            }
        }

        private static void ApplyLlmTextCompletion(MetadataResult<Series> result, LlmScrapingAssistResult llmAssistResult)
        {
            if (result.Item == null || !result.HasMetadata || llmAssistResult.Status != LlmScrapingAssistStatus.Succeeded)
            {
                return;
            }

            _ = new LlmMetadataMergePolicy().Apply(result, llmAssistResult.Suggestion, Config);
        }

        private async Task<LlmScrapingAssistResult> TryAssistSeriesMetadataWithLlmAsync(SeriesInfo info, DefaultScraperSemantic semantic, CancellationToken cancellationToken)
        {
            if (this.llmMetadataAssistService == null || !Config.LlmAllowTextCompletion || !HasCompleteLlmConfiguration(Config))
            {
                return LlmScrapingAssistResult.NotTriggered("LlmConfigurationMissing");
            }

            var triggerDecision = new LlmAssistTriggerPolicy().Evaluate(new LlmAssistTriggerContext
            {
                Configuration = Config,
                Semantic = semantic,
                MediaType = nameof(Series),
                IsImageProvider = false,
                HttpContext = this.HttpContextAccessor.HttpContext,
            });
            if (!triggerDecision.ShouldTrigger)
            {
                return LlmScrapingAssistResult.NotTriggered(triggerDecision.Reason);
            }

            return await this.llmMetadataAssistService.AssistAsync(
                new LlmScrapingAssistRequest
                {
                    Configuration = Config,
                    LookupInfo = CreateSafeLlmSeriesLookupInfo(info),
                    MediaType = nameof(Series),
                    Semantic = semantic,
                    IsImageProvider = false,
                    HttpContext = this.HttpContextAccessor.HttpContext,
                    LibraryRoots = Array.Empty<string?>(),
                },
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<LlmExternalIdResolutionResult> TryResolveSeriesExternalIdsWithLlmAsync(SeriesInfo info, DefaultScraperSemantic semantic, CancellationToken cancellationToken)
        {
            if (this.llmExternalIdResolutionService == null || !HasCompleteLlmConfiguration(Config))
            {
                return LlmExternalIdResolutionResult.NotTriggered("LlmConfigurationMissing");
            }

            var triggerDecision = new LlmAssistTriggerPolicy().Evaluate(new LlmAssistTriggerContext
            {
                Configuration = Config,
                Semantic = semantic,
                MediaType = nameof(Series),
                IsImageProvider = false,
                HttpContext = this.HttpContextAccessor.HttpContext,
            });
            if (!triggerDecision.ShouldTrigger)
            {
                return LlmExternalIdResolutionResult.NotTriggered(triggerDecision.Reason);
            }

            return await this.llmExternalIdResolutionService.ResolveAsync(
                new LlmExternalIdResolutionRequest
                {
                    Configuration = Config,
                    LookupInfo = CreateSafeLlmExternalIdSeriesLookupInfo(info),
                    MediaType = nameof(Series),
                    Name = info.Name,
                    Year = info.Year,
                    Semantic = semantic,
                    IsImageProvider = false,
                    HttpContext = this.HttpContextAccessor.HttpContext,
                    LibraryRoots = Array.Empty<string?>(),
                },
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<LlmTmdbIdCorrectionResult> TryResolveSeriesTmdbCorrectionAsync(SeriesInfo info, DefaultScraperSemantic semantic, string? originalTmdbId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(originalTmdbId)
                || this.llmExternalIdResolutionService == null
                || !Config.EnableLlmTmdbIdCorrection
                || !HasCompleteLlmConfiguration(Config))
            {
                return LlmTmdbIdCorrectionResult.NoReplacement("LlmTmdbIdCorrectionConfigurationMissing");
            }

            var triggerDecision = this.llmTmdbIdCorrectionTriggerPolicy.Evaluate(new LlmAssistTriggerContext
            {
                Configuration = Config,
                Semantic = semantic,
                MediaType = nameof(Series),
                IsImageProvider = false,
                HttpContext = this.HttpContextAccessor.HttpContext,
            });
            if (!triggerDecision.ShouldTrigger)
            {
                return LlmTmdbIdCorrectionResult.NoReplacement(triggerDecision.Reason);
            }

            var lookupInfo = CreateSafeLlmTmdbCorrectionSeriesLookupInfo(info);
            var relativePathSamples = string.IsNullOrWhiteSpace(lookupInfo.Path)
                ? Array.Empty<string?>()
                : new[] { lookupInfo.Path };

            return await this.llmExternalIdResolutionService.TryResolveTmdbCorrectionAsync(
                new LlmTmdbIdCorrectionRequest
                {
                    Configuration = Config,
                    LookupInfo = lookupInfo,
                    MediaType = nameof(Series),
                    OldTmdbId = originalTmdbId,
                    Name = info.Name,
                    Year = info.Year,
                    Semantic = semantic,
                    IsImageProvider = false,
                    HttpContext = this.HttpContextAccessor.HttpContext,
                    LibraryRoots = Array.Empty<string?>(),
                    RelativePathSamples = relativePathSamples,
                },
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<string?> GuessByDoubanWithLlmHintsAsync(LlmSearchHints searchHints, ItemLookupInfo info, CancellationToken cancellationToken)
        {
            var title = NormalizeLlmHintText(searchHints.Title);
            if (title == null)
            {
                return null;
            }

            var year = searchHints.Year;
            this.Log("使用 LLM 提示搜索 Douban 剧集. title: {0} year: {1}", title, year);

            if (Config.EnableDoubanAvoidRiskControl && year != null && year > 0)
            {
                var suggestResults = await this.DoubanApi.SearchBySuggestAsync(title, cancellationToken).ConfigureAwait(false);
                var suggestMatch = suggestResults.FirstOrDefault(x => x.Year == year && string.Equals(x.Name, title, StringComparison.OrdinalIgnoreCase))
                    ?? suggestResults.FirstOrDefault(x => x.Year == year);
                if (suggestMatch != null)
                {
                    this.Log("已通过 LLM 提示找到 Douban id（suggest）. name: {0} sid: {1}", suggestMatch.Name, suggestMatch.Sid);
                    return suggestMatch.Sid;
                }
            }

            var results = await this.DoubanApi.SearchAsync(title, cancellationToken).ConfigureAwait(false);
            var item = year != null && year > 0
                ? results.FirstOrDefault(x => x.Category == "电视剧" && x.Year == year)
                : results.FirstOrDefault(x => x.Category == "电视剧");
            if (item == null)
            {
                return null;
            }

            this.Log("已通过 LLM 提示找到 Douban id. name: {0} sid: {1}", item.Name, item.Sid);
            return item.Sid;
        }

        private async Task<string?> GuessByTmdbWithLlmHintsAsync(LlmSearchHints searchHints, ItemLookupInfo info, CancellationToken cancellationToken)
        {
            var title = NormalizeLlmHintText(searchHints.Title);
            if (title == null)
            {
                return null;
            }

            this.Log("使用 LLM 提示搜索 TMDb 剧集. title: {0} year: {1}", title, searchHints.Year);
            return await this.GuestByTmdbAsync(title, searchHints.Year, info, cancellationToken).ConfigureAwait(false);
        }

        private async Task TryAssistEpisodeGroupMappingWithLlmAsync(string? tmdbId, string? seriesTitle, SeriesInfo info, DefaultScraperSemantic semantic, CancellationToken cancellationToken)
        {
            if (this.llmEpisodeGroupMappingProviderAssistService == null
                || !int.TryParse(tmdbId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seriesTmdbId))
            {
                return;
            }

            var safePath = Config.LlmAllowRelativePathContext
                ? LlmRelativePathSanitizer.Sanitize(info.Path, Array.Empty<string?>(), nameof(Series))
                : string.Empty;
            await this.llmEpisodeGroupMappingProviderAssistService.SuggestWriteAndRefreshAsync(
                    new LlmEpisodeGroupMappingProviderAssistRequest
                    {
                        Configuration = Config,
                        SeriesTmdbId = seriesTmdbId,
                        SeriesTitle = string.IsNullOrWhiteSpace(seriesTitle) ? info.Name : seriesTitle,
                        MetadataLanguage = info.MetadataLanguage,
                        MediaType = nameof(Series),
                        Semantic = semantic,
                        HttpContext = this.HttpContextAccessor.HttpContext,
                        SafeRelativePathSamples = string.IsNullOrWhiteSpace(safePath) ? Array.Empty<string?>() : new[] { safePath },
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private static bool TryResolveItemIdFromRequestPath(HttpContext? httpContext, out Guid itemId)
        {
            itemId = Guid.Empty;

            var path = httpContext?.Request.Path.Value;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length >= 2
                && string.Equals(segments[0], "Items", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(segments[1], out itemId);
        }

        private static bool IsExplicitRefreshRequestWithoutReplaceAllMetadata(HttpContext? httpContext, Guid expectedItemId)
        {
            if (httpContext == null
                || expectedItemId == Guid.Empty
                || !HttpMethods.IsPost(httpContext.Request.Method))
            {
                return false;
            }

            var path = httpContext.Request.Path.Value;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 3
                || !string.Equals(segments[0], "Items", StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParse(segments[1], out var requestItemId)
                || requestItemId != expectedItemId
                || !string.Equals(segments[2], "Refresh", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (OverwriteMetadataRefreshClassifier.IsOverwriteMetadataRefresh(httpContext, expectedItemId))
            {
                return false;
            }

            return true;
        }

        private static bool ShouldRearmQueuedOverwriteCandidate(DefaultScraperSemantic semantic, HttpContext? httpContext, Guid expectedItemId)
        {
            if (semantic == DefaultScraperSemantic.ManualMatch)
            {
                return true;
            }

            if (semantic == DefaultScraperSemantic.OverwriteRefresh)
            {
                return false;
            }

            if (semantic != DefaultScraperSemantic.UserRefresh)
            {
                return false;
            }

            return httpContext == null || IsExplicitRefreshRequestWithoutReplaceAllMetadata(httpContext, expectedItemId);
        }

        private static string FormatProviderIdForLog(string? providerId)
        {
            if (providerId == null)
            {
                return "'<null>'";
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "'{0}'",
                providerId
                    .Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\r", "\\r", StringComparison.Ordinal)
                    .Replace("\n", "\\n", StringComparison.Ordinal)
                    .Replace("\t", "\\t", StringComparison.Ordinal)
                    .Replace(" ", "\\u0020", StringComparison.Ordinal));
        }

        private RemoteSearchResult MapTmdbSeriesSearchResult(SearchTv searchResult)
        {
            return this.MapTmdbSeriesSearchResult(
                searchResult.Id,
                searchResult.Name,
                searchResult.OriginalName,
                searchResult.PosterPath,
                searchResult.Overview,
                searchResult.FirstAirDate);
        }

        private RemoteSearchResult MapTmdbSeriesSearchResult(TvShow seriesResult)
        {
            return this.MapTmdbSeriesSearchResult(
                seriesResult.Id,
                seriesResult.Name,
                seriesResult.OriginalName,
                seriesResult.PosterPath,
                seriesResult.Overview,
                seriesResult.FirstAirDate);
        }

        private RemoteSearchResult MapTmdbSeriesSearchResult(int tmdbId, string? name, string? originalName, string? posterPath, string? overview, DateTime? firstAirDate)
        {
            return new RemoteSearchResult
            {
                // 这里 MetaSharkPlugin.ProviderId 的值做这么复杂，是为了和电影保持一致并唯一
                ProviderIds = new Dictionary<string, string>
                {
                    { MetadataProvider.Tmdb.ToString(), tmdbId.ToString(CultureInfo.InvariantCulture) },
                    { MetaSharkPlugin.ProviderId, $"Tmdb_{tmdbId}" },
                },
                Name = string.Format(CultureInfo.InvariantCulture, "[TMDB]{0}", name ?? originalName),
                ImageUrl = string.IsNullOrEmpty(posterPath) ? null : this.TmdbApi.GetPosterUrl(posterPath)?.ToString(),
                Overview = overview,
                ProductionYear = firstAirDate?.Year,
            };
        }

        private async Task<MetadataResult<Series>> GetMetadataByTmdb(string? tmdbId, ItemLookupInfo info, CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Series>();
            if (string.IsNullOrEmpty(tmdbId))
            {
                return result;
            }

            this.Log("通过 TMDb 获取剧集元数据. tmdbId: \"{0}\"", tmdbId);
            var tvShow = await this.TmdbApi
                .GetSeriesAsync(Convert.ToInt32(tmdbId, CultureInfo.InvariantCulture), info.MetadataLanguage, info.MetadataLanguage, cancellationToken)
                .ConfigureAwait(false);

            if (tvShow == null)
            {
                return result;
            }

            result = new MetadataResult<Series>
            {
                Item = this.MapTvShowToSeries(tvShow, info.MetadataCountryCode),
                ResultLanguage = info.MetadataLanguage ?? tvShow.OriginalLanguage,
            };

            var acceptedPeopleCount = await this.AddTmdbPeopleAsync(tvShow, result, cancellationToken).ConfigureAwait(false);
            this.TryQueueSearchMissingMetadataOverwriteCandidate(info, tmdbId, result.People, acceptedPeopleCount);

            result.QueriedById = true;
            result.HasMetadata = true;
            return result;
        }

        private async Task<int> TryAddTmdbPeopleAsync(string tmdbId, ItemLookupInfo info, MetadataResult<Series> result, CancellationToken cancellationToken)
        {
            if (!int.TryParse(tmdbId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbNumericId))
            {
                return 0;
            }

            var tvShow = await this.TmdbApi
                .GetSeriesAsync(tmdbNumericId, info.MetadataLanguage, info.MetadataLanguage, cancellationToken)
                .ConfigureAwait(false);

            if (tvShow == null)
            {
                return 0;
            }

            return await this.AddTmdbPeopleAsync(tvShow, result, cancellationToken).ConfigureAwait(false);
        }

        private async Task<int> AddTmdbPeopleAsync(TvShow tvShow, MetadataResult<Series> result, CancellationToken cancellationToken)
        {
            var people = await this.GetPersonsAsync(tvShow, cancellationToken).ConfigureAwait(false);
            foreach (var person in people)
            {
                result.AddPerson(person);
            }

            return people.Count;
        }

        private async Task<string?> FindTmdbId(string name, string imdb, int? year, ItemLookupInfo info, CancellationToken cancellationToken)
        {
            // 通过imdb获取TMDB id
            if (!string.IsNullOrEmpty(imdb))
            {
                var tmdbId = await this.GetTmdbIdByImdbAsync(imdb, info.MetadataLanguage, info, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(tmdbId))
                {
                    return tmdbId;
                }
                else
                {
                    this.Log("未找到 TMDb id. imdbId: \"{0}\"", imdb);
                }
            }

            // 尝试通过搜索匹配获取tmdbId
            if (!string.IsNullOrEmpty(name) && year != null && year > 0)
            {
                var tmdbId = await this.GuestByTmdbAsync(name, year, info, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(tmdbId))
                {
                    return tmdbId;
                }
                else
                {
                    this.Log("未找到 TMDb id. name: \"{0}\" year: \"{1}\"", name, year);
                }
            }

            return null;
        }

        private void TryQueueSearchMissingMetadataOverwriteCandidate(ItemLookupInfo info, string tmdbId, IEnumerable<PersonInfo>? authoritativePeople, int expectedPeopleCount)
        {
            if (this.movieSeriesPeopleOverwriteRefreshCandidateStore == null)
            {
                return;
            }

            var httpContext = this.HttpContextAccessor.HttpContext;
            var semantic = this.ResolveMetadataSemantic(info);
            if (!SupportsSearchMissingMetadataOverwriteCandidate(semantic))
            {
                return;
            }

            var series = !string.IsNullOrWhiteSpace(info.Path)
                ? this.LibraryManager.FindByPath(info.Path, true) as Series
                : null;
            var authoritativePeopleSnapshot = CreateTmdbAuthoritativePeopleSnapshot(nameof(Series), tmdbId, authoritativePeople);
            if (authoritativePeopleSnapshot == null
                || !RequiresSearchMissingMetadataOverwriteCandidate(series, authoritativePeopleSnapshot))
            {
                return;
            }

            var itemId = series?.Id ?? Guid.Empty;
            if (itemId == Guid.Empty && !TryResolveItemIdFromRequestPath(httpContext, out itemId))
            {
                return;
            }

            var existingCandidate = this.movieSeriesPeopleOverwriteRefreshCandidateStore.Peek(itemId);
            if (existingCandidate?.OverwriteQueued == true
                && !ShouldRearmQueuedOverwriteCandidate(semantic, httpContext, itemId))
            {
                return;
            }

            this.movieSeriesPeopleOverwriteRefreshCandidateStore.Save(new MovieSeriesPeopleOverwriteRefreshCandidate
            {
                ItemId = itemId,
                ItemPath = series?.Path ?? info.Path ?? string.Empty,
                ExpectedPeopleCount = expectedPeopleCount,
                AuthoritativePeopleSnapshot = authoritativePeopleSnapshot,
            });
            this.Log(
                "已记录单项影视人物 overwrite candidate. itemId: {0} semantic: {1} expectedPeopleCount: {2} authoritativePeopleCount: {3}",
                itemId,
                semantic,
                expectedPeopleCount,
                authoritativePeopleSnapshot.People.Count);
        }

        private string? GetTmdbOfficialRatingByData(TvShow? tvShow, string preferredCountryCode)
        {
            _ = this.Logger;
            if (tvShow != null)
            {
                var contentRatings = tvShow.ContentRatings.Results ?? new List<ContentRating>();

                var ourRelease = contentRatings.FirstOrDefault(c => string.Equals(c.Iso_3166_1, preferredCountryCode, StringComparison.OrdinalIgnoreCase));
                var usRelease = contentRatings.FirstOrDefault(c => string.Equals(c.Iso_3166_1, "US", StringComparison.OrdinalIgnoreCase));
                var minimumRelease = contentRatings.FirstOrDefault();

                if (ourRelease != null)
                {
                    return ourRelease.Rating;
                }
                else if (usRelease != null)
                {
                    return usRelease.Rating;
                }
                else if (minimumRelease != null)
                {
                    return minimumRelease.Rating;
                }
            }

            return null;
        }

        private Series MapTvShowToSeries(TvShow seriesResult, string preferredCountryCode)
        {
            var series = new Series
            {
                Name = seriesResult.Name,
                OriginalTitle = seriesResult.OriginalName,
            };

            series.SetProviderId(MetadataProvider.Tmdb, seriesResult.Id.ToString(CultureInfo.InvariantCulture));

            series.CommunityRating = (float)System.Math.Round(seriesResult.VoteAverage, 2);

            series.Overview = seriesResult.Overview;

            if (seriesResult.Networks != null)
            {
                series.Studios = seriesResult.Networks.Select(i => i.Name).ToArray();
            }

            if (seriesResult.Genres != null)
            {
                series.Genres = seriesResult.Genres.Select(i => i.Name).ToArray();
            }

            if (Config.EnableTmdbTags && seriesResult.Keywords?.Results != null)
            {
                var tagCount = seriesResult.Keywords.Results.Count;
                for (var i = 0; i < seriesResult.Keywords.Results.Count; i++)
                {
                    series.AddTag(seriesResult.Keywords.Results[i].Name);
                }

                if (tagCount > 0)
                {
                    this.Log("已写入剧集 TMDb 标签. id={0} name={1} count={2}", seriesResult.Id, seriesResult.Name, tagCount);
                }
            }

            series.HomePageUrl = seriesResult.Homepage;

            series.RunTimeTicks = seriesResult.EpisodeRunTime.Select(i => TimeSpan.FromMinutes(i).Ticks).FirstOrDefault();

            if (string.Equals(seriesResult.Status, "Ended", StringComparison.OrdinalIgnoreCase))
            {
                series.Status = SeriesStatus.Ended;
                series.EndDate = seriesResult.LastAirDate;
            }
            else
            {
                series.Status = SeriesStatus.Continuing;
            }

            series.PremiereDate = seriesResult.FirstAirDate;
            series.ProductionYear = seriesResult.FirstAirDate?.Year;

            var ids = seriesResult.ExternalIds;
            if (ids != null)
            {
                if (!string.IsNullOrWhiteSpace(ids.ImdbId))
                {
                    series.SetProviderId(MetadataProvider.Imdb, ids.ImdbId);
                }

                if (!string.IsNullOrEmpty(ids.TvrageId))
                {
                    series.SetProviderId(MetadataProvider.TvRage, ids.TvrageId);
                }

                if (!string.IsNullOrEmpty(ids.TvdbId))
                {
                    series.SetProviderId(MetadataProvider.Tvdb, ids.TvdbId);
                }
            }

            series.SetProviderId(MetaSharkPlugin.ProviderId, $"{MetaSource.Tmdb}_{seriesResult.Id}");
            series.OfficialRating = this.GetTmdbOfficialRatingByData(seriesResult, preferredCountryCode);

            return series;
        }

        private async Task<string?> GetTmdbOfficialRating(ItemLookupInfo info, string tmdbId, CancellationToken cancellationToken)
        {
            var tvShow = await this.TmdbApi
                            .GetSeriesAsync(Convert.ToInt32(tmdbId, CultureInfo.InvariantCulture), info.MetadataLanguage, info.MetadataLanguage, cancellationToken)
                            .ConfigureAwait(false);
            return this.GetTmdbOfficialRatingByData(tvShow, info.MetadataCountryCode);
        }

        private async Task TryPopulateTvExternalIdsFromTmdbAsync(Series series, string tmdbId, ItemLookupInfo info, CancellationToken cancellationToken)
        {
            if (!int.TryParse(tmdbId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbNumericId))
            {
                return;
            }

            var tvShow = await this.TmdbApi
                .GetSeriesAsync(tmdbNumericId, info.MetadataLanguage, info.MetadataLanguage, cancellationToken)
                .ConfigureAwait(false);

            var externalIds = tvShow?.ExternalIds;
            if (externalIds == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(series.GetProviderId(MetadataProvider.Imdb)) && !string.IsNullOrWhiteSpace(externalIds.ImdbId))
            {
                series.SetProviderId(MetadataProvider.Imdb, externalIds.ImdbId);
            }

            if (string.IsNullOrWhiteSpace(series.GetProviderId(MetadataProvider.Tvdb)) && !string.IsNullOrWhiteSpace(externalIds.TvdbId))
            {
                series.SetProviderId(MetadataProvider.Tvdb, externalIds.TvdbId);
                this.Log("已通过 TMDb 外部 ID 写入剧集 TVDB id. tmdbId: {0} tvdbId: {1}", tmdbId, externalIds.TvdbId);
            }
        }

        private async Task<IReadOnlyList<PersonInfo>> GetPersonsAsync(TvShow seriesResult, CancellationToken cancellationToken)
        {
            var persons = new List<PersonInfo>();

            if (seriesResult.AggregateCredits?.Cast != null && seriesResult.AggregateCredits.Cast.Count > 0)
            {
                var acceptedActorCount = 0;

                foreach (var actor in seriesResult.AggregateCredits.Cast)
                {
                    if (acceptedActorCount >= Configuration.PluginConfiguration.MAXCASTMEMBERS)
                    {
                        break;
                    }

                    var localizedName = await this.ResolveSimplifiedChineseOnlyItemPersonNameAsync(actor.Name, actor.Id, cancellationToken).ConfigureAwait(false);
                    if (localizedName == null)
                    {
                        continue;
                    }

                    var personInfo = new PersonInfo
                    {
                        Name = localizedName,
                        Role = actor.Roles?.FirstOrDefault(role => !string.IsNullOrWhiteSpace(role.Character))?.Character,
                        Type = PersonKind.Actor,
                        SortOrder = acceptedActorCount,
                    };

                    if (!string.IsNullOrWhiteSpace(actor.ProfilePath))
                    {
                        personInfo.ImageUrl = this.TmdbApi.GetProfileUrl(actor.ProfilePath)?.ToString();
                    }

                    if (actor.Id > 0)
                    {
                        personInfo.SetProviderId(MetadataProvider.Tmdb, actor.Id.ToString(CultureInfo.InvariantCulture));
                    }

                    persons.Add(personInfo);
                    acceptedActorCount++;
                }

                return persons;
            }

            // 演员
            if (seriesResult.Credits?.Cast != null)
            {
                var acceptedActorCount = 0;

                foreach (var actor in seriesResult.Credits.Cast.OrderBy(a => a.Order))
                {
                    if (acceptedActorCount >= Configuration.PluginConfiguration.MAXCASTMEMBERS)
                    {
                        break;
                    }

                    var localizedName = await this.ResolveSimplifiedChineseOnlyItemPersonNameAsync(actor.Name, actor.Id, cancellationToken).ConfigureAwait(false);
                    if (localizedName == null)
                    {
                        continue;
                    }

                    var personInfo = new PersonInfo
                    {
                        Name = localizedName,
                        Role = actor.Character,
                        Type = PersonKind.Actor,
                        SortOrder = actor.Order,
                    };

                    if (!string.IsNullOrWhiteSpace(actor.ProfilePath))
                    {
                        personInfo.ImageUrl = this.TmdbApi.GetProfileUrl(actor.ProfilePath)?.ToString();
                    }

                    if (actor.Id > 0)
                    {
                        personInfo.SetProviderId(MetadataProvider.Tmdb, actor.Id.ToString(CultureInfo.InvariantCulture));
                    }

                    persons.Add(personInfo);
                    acceptedActorCount++;
                }
            }

            return persons;
        }
    }
}
