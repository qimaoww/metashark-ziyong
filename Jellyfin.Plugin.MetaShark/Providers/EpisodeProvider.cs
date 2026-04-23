// <copyright file="EpisodeProvider.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Xml;
    using System.Xml.Linq;
    using Jellyfin.Plugin.MetaShark.Api;
    using Jellyfin.Plugin.MetaShark.Core;
    using Jellyfin.Plugin.MetaShark.Model;
    using Jellyfin.Plugin.MetaShark.Workers;
    using Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Providers;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Caching.Memory;
    using Microsoft.Extensions.Logging;

    public class EpisodeProvider : BaseProvider, IRemoteMetadataProvider<Episode, EpisodeInfo>, IDisposable
    {
        private static readonly Action<ILogger, string, int, int, string, Exception?> LogTvdbPlacementLookup =
            LoggerMessage.Define<string, int, int, string>(LogLevel.Debug, new EventId(2, nameof(LogTvdbPlacementLookup)), "[MetaShark] 查询 TVDB 特别篇定位. tvdbId={TvdbId} s{Season}e{Episode} lang={Lang}");

        private static readonly Action<ILogger, string, int, int, Exception?> LogTvdbPlacementNotFound =
            LoggerMessage.Define<string, int, int>(LogLevel.Debug, new EventId(3, nameof(LogTvdbPlacementNotFound)), "[MetaShark] 未找到 TVDB 特别篇定位. tvdbId={TvdbId} s{Season}e{Episode}");

        private static readonly Action<ILogger, string, int, Exception?> LogTvdbPlacementInvalidInput =
            LoggerMessage.Define<string, int>(LogLevel.Debug, new EventId(4, nameof(LogTvdbPlacementInvalidInput)), "[MetaShark] TVDB 特别篇定位入参无效. tvdbId={TvdbId} episode={Episode}");

        private static readonly Action<ILogger, string, Exception?> LogTvdbPlacementEmptyList =
            LoggerMessage.Define<string>(LogLevel.Debug, new EventId(5, nameof(LogTvdbPlacementEmptyList)), "[MetaShark] TVDB 特别篇列表为空. tvdbId={TvdbId}");

        private static readonly Action<ILogger, string, int, Exception?> LogTvdbPlacementNoMatch =
            LoggerMessage.Define<string, int>(LogLevel.Debug, new EventId(6, nameof(LogTvdbPlacementNoMatch)), "[MetaShark] TVDB 特别篇列表未命中. tvdbId={TvdbId} episode={Episode}");

        private static readonly Action<ILogger, string, Exception?> LogTvdbIdMissing =
            LoggerMessage.Define<string>(LogLevel.Debug, new EventId(7, nameof(LogTvdbIdMissing)), "[MetaShark] 未找到剧集 TVDB id. source={Source}");

        private static readonly Action<ILogger, string, Exception?> LogTvdbIdResolved =
            LoggerMessage.Define<string>(LogLevel.Debug, new EventId(8, nameof(LogTvdbIdResolved)), "[MetaShark] 已解析剧集 TVDB id. tvdbId={TvdbId}");

        private static readonly HashSet<char> SimplifiedOverviewScriptDistinctiveCharacters = new HashSet<char>("个么乐习书亲众优伤儿这来为们让带开车辆两厉讲较听说点体与无龙猫坏关级评论丰围绕争夺复选战遗嘱发间医会现导经过国际组织怀惊计划实验档录历样欢觉观记议语误读轻还迟释难顺须顾顿预领题额颜风飞宫归马讶");
        private static readonly HashSet<char> TraditionalOverviewScriptDistinctiveCharacters = new HashSet<char>("個麼樂習書親眾優傷兒這來為們讓帶開車輛兩厲講較聽說點體與無龍貓壞關級評論豐圍繞爭奪複選戰遺囑發間醫會現導經過國際組織懷驚計畫實驗檔錄歷樣歡覺觀記議語誤讀輕還遲釋難順須顧頓預領題額顏風飛宮歸馬訝");

        private readonly MemoryCache memoryCache;
        private readonly EpisodeTitleBackfillCoordinator? episodeTitleBackfillCoordinator;
        private readonly IEpisodeOverviewCleanupCandidateStore? episodeOverviewCleanupCandidateStore;
        private readonly TvdbApi tvdbApi;

        public EpisodeProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, DoubanApi doubanApi, TmdbApi tmdbApi, OmdbApi omdbApi, ImdbApi imdbApi, TvdbApi tvdbApi, IEpisodeTitleBackfillCandidateStore? episodeTitleBackfillCandidateStore = null)
            : this(httpClientFactory, loggerFactory, libraryManager, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi, tvdbApi, episodeTitleBackfillCandidateStore, null)
        {
        }

        public EpisodeProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, DoubanApi doubanApi, TmdbApi tmdbApi, OmdbApi omdbApi, ImdbApi imdbApi, TvdbApi tvdbApi, IEpisodeTitleBackfillCandidateStore? episodeTitleBackfillCandidateStore, IEpisodeOverviewCleanupCandidateStore? episodeOverviewCleanupCandidateStore)
            : base(httpClientFactory, loggerFactory.CreateLogger<EpisodeProvider>(), libraryManager, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi)
        {
            this.memoryCache = new MemoryCache(new MemoryCacheOptions());
            this.episodeTitleBackfillCoordinator = episodeTitleBackfillCandidateStore != null ? new EpisodeTitleBackfillCoordinator(episodeTitleBackfillCandidateStore, this.Logger) : null;
            this.episodeOverviewCleanupCandidateStore = episodeOverviewCleanupCandidateStore;
            this.tvdbApi = tvdbApi;
        }

        internal enum SearchMissingMetadataOverviewCleanupReason
        {
            RequestNotSearchMissingMetadata = 0,
            EpisodeIdMissing = 1,
            TrustedOverviewPresent = 2,
            OriginalOverviewSnapshotPresent = 3,
            CandidateQueued = 4,
        }

        public string Name => MetaSharkPlugin.PluginName;

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(searchInfo);
            this.Log("开始搜索剧集单集候选. name: {0}", searchInfo.Name);
            return await Task.FromResult(Enumerable.Empty<RemoteSearchResult>()).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<MetadataResult<Episode>> GetMetadata(EpisodeInfo info, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(info);

            // 刷新元数据四种模式差别：
            // 自动扫描匹配：info的Name、IndexNumber和ParentIndexNumber是从文件名解析出来的，假如命名不规范，就会导致解析出错误值
            // 识别：info的Name、IndexNumber和ParentIndexNumber是从文件名解析出来的，provinceIds有指定选择项的ProvinceId
            // 覆盖所有元数据：info的Name、IndexNumber和ParentIndexNumber是从文件名解析出来的，provinceIds保留所有旧值
            // 搜索缺少的元数据：info的Name、IndexNumber和ParentIndexNumber是从当前的元数据获取，provinceIds保留所有旧值
            var fileName = Path.GetFileName(info.Path);
            this.Log("开始获取单集元数据. name: {0} fileName: {1} episodeNumber: {2} seasonNumber: {3} isMissingEpisode: {4} enableTmdb: {5} displayOrder: {6}", info.Name, fileName, info.IndexNumber, info.ParentIndexNumber, info.IsMissingEpisode, Config.EnableTmdb, info.SeriesDisplayOrder);
            var result = new MetadataResult<Episode>();

            // Allowing this will dramatically increase scan times
            if (info.IsMissingEpisode)
            {
                return result;
            }

            // 动画特典和extras处理
            var specialResult = this.HandleAnimeExtras(info);
            if (specialResult != null)
            {
                return specialResult;
            }

            var originalMetadataTitle = info.Name;

            // 使用AnitomySharp进行重新解析，解决anime识别错误
            info = this.FixParseInfo(info);

            // 剧集信息只有tmdb有
            info.SeriesProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var seriesTmdbId);
            var seasonNumber = info.ParentIndexNumber;
            var episodeNumber = info.IndexNumber;
            result.HasMetadata = true;
            result.Item = new Episode
            {
                ParentIndexNumber = seasonNumber,
                IndexNumber = episodeNumber,
                Name = originalMetadataTitle,
            };

            if (episodeNumber is null or 0 || seasonNumber is null || string.IsNullOrEmpty(seriesTmdbId))
            {
                this.Log("缺少单集元数据. episodeNumber: {0} seasonNumber: {1} seriesTmdbId: {2}", episodeNumber, seasonNumber, seriesTmdbId);
                return result;
            }

            var episodeItem = this.LibraryManager.FindByPath(info.Path, false) as Episode;
            var requestedMetadataLanguage = info.MetadataLanguage ?? episodeItem?.GetPreferredMetadataLanguage();
            var titleMetadataLanguage = ResolveEpisodeTargetMetadataLanguage(requestedMetadataLanguage);

            var episodeResult = await this.GetEpisodeAsync(
                    seriesTmdbId.ToInt(),
                    seasonNumber,
                    episodeNumber,
                    info.SeriesDisplayOrder,
                    titleMetadataLanguage,
                    titleMetadataLanguage,
                    cancellationToken)
                .ConfigureAwait(false);
            if (episodeResult == null)
            {
                this.Log("未找到 TMDb 单集数据. seriesTmdbId: {0} seasonNumber: {1} episodeNumber: {2} displayOrder: {3}", seriesTmdbId, seasonNumber, episodeNumber, info.SeriesDisplayOrder);
                return result;
            }

            result.HasMetadata = true;
            result.QueriedById = true;

            var seriesItem = episodeItem?.Series;
            var seriesPath = this.GetOriginalSeriesPath(info);
            if (seriesItem == null && !string.IsNullOrWhiteSpace(seriesPath))
            {
                seriesItem = this.LibraryManager.FindByPath(seriesPath, true) as Series;
            }

            var seriesOverview = seriesItem?.Overview;
            var seasonPath = this.GetOriginalSeasonPath(info);
            var seasonItem = !string.IsNullOrWhiteSpace(seasonPath) ? this.LibraryManager.FindByPath(seasonPath, true) as Season : null;
            var seasonOverview = seasonItem?.Overview;
            var titleResolution = await this.ResolveEffectiveEpisodeProviderTitleAsync(
                    seriesTmdbId.ToInt(),
                    seasonNumber,
                    episodeNumber,
                    info.SeriesDisplayOrder,
                    titleMetadataLanguage,
                    info.MetadataLanguage,
                    episodeResult.Name,
                    cancellationToken)
                .ConfigureAwait(false);
            var effectiveProviderTitle = titleResolution.EffectiveProviderTitle;
            var detailsOverview = TrimEpisodeLocalizedValue(CreateEpisodeLocalizedValue(
                episodeResult.Overview,
                ResolveEpisodeOverviewSourceLanguage(titleMetadataLanguage)));
            var translationOverview = await this.GetEpisodeTranslationOverviewAsync(
                    seriesTmdbId.ToInt(),
                    seasonNumber,
                    episodeNumber,
                    info.SeriesDisplayOrder,
                    titleMetadataLanguage,
                    info.MetadataLanguage,
                    cancellationToken)
                .ConfigureAwait(false);
            var overviewDecision = ResolveEpisodeOverviewPersistence(translationOverview?.SourceLanguage, translationOverview?.Value, seriesOverview, seasonOverview);
            if (overviewDecision.Overview == null)
            {
                overviewDecision = ResolveEpisodeOverviewPersistence(detailsOverview?.SourceLanguage, detailsOverview?.Value, seriesOverview, seasonOverview);
            }

            var item = new Episode
            {
                IndexNumber = episodeNumber,
                ParentIndexNumber = seasonNumber,
            };

            item.Overview = overviewDecision.Overview;

            if (!string.IsNullOrEmpty(overviewDecision.ResultLanguage))
            {
                result.ResultLanguage = overviewDecision.ResultLanguage;
            }

            if (seasonNumber == 0 && Config.EnableTvdbSpecialsWithinSeasons)
            {
                var seriesTvdbId = await this.ResolveSeriesTvdbIdAsync(info, seriesTmdbId, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(seriesTvdbId))
                {
                    LogTvdbPlacementLookup(
                        this.Logger,
                        seriesTvdbId,
                        seasonNumber.Value,
                        episodeNumber.Value,
                        info.MetadataLanguage ?? string.Empty,
                        null);
                    var placement = await this.TryBuildTvdbSpecialPlacementAsync(seriesTvdbId, episodeNumber, info.MetadataLanguage, cancellationToken)
                        .ConfigureAwait(false);
                    if (placement != null)
                    {
                        item.AirsBeforeSeasonNumber = placement.AirsBeforeSeasonNumber;
                        item.AirsBeforeEpisodeNumber = placement.AirsBeforeEpisodeNumber;
                        item.AirsAfterSeasonNumber = placement.AirsAfterSeasonNumber;
                        this.Log(
                            "TVDB 特别篇定位结果. tvdbId: {0} s{1}e{2} -> beforeSeason: {3} beforeEpisode: {4} afterSeason: {5}",
                            seriesTvdbId,
                            seasonNumber,
                            episodeNumber,
                            item.AirsBeforeSeasonNumber,
                            item.AirsBeforeEpisodeNumber,
                            item.AirsAfterSeasonNumber);
                    }
                    else
                    {
                        LogTvdbPlacementNotFound(this.Logger, seriesTvdbId, seasonNumber.Value, episodeNumber.Value, null);
                    }
                }
                else
                {
                    this.Log("跳过 TVDB 特别篇定位，缺少 TVDB id. s{0}e{1}", seasonNumber, episodeNumber);
                }
            }

            item.PremiereDate = episodeResult.AirDate;
            item.ProductionYear = episodeResult.AirDate?.Year;
            item.Name = ResolveEpisodeTitlePersistence(originalMetadataTitle, effectiveProviderTitle);
            item.CommunityRating = (float)System.Math.Round(episodeResult.VoteAverage, 1);

            var httpContext = this.HttpContextAccessor.HttpContext;
            var metadataRefreshMode = httpContext?.Request.Query["metadataRefreshMode"].ToString();
            var replaceAllMetadata = httpContext?.Request.Query["replaceAllMetadata"].ToString();
            var hasExplicitSearchMissingMetadataQuery = EpisodeTitleBackfillRefreshClassifier.HasSearchMissingMetadataRefreshQuery(httpContext);
            var isImplicitSearchMissingMetadataFallback = !hasExplicitSearchMissingMetadataQuery
                && EpisodeTitleBackfillRefreshClassifier.ShouldFallbackSearchMissingMetadataRefresh(originalMetadataTitle, episodeItem?.Name);
            var isSearchMissingMetadataRequest = hasExplicitSearchMissingMetadataQuery
                ? EpisodeTitleBackfillRefreshClassifier.IsSearchMissingMetadataRefresh(metadataRefreshMode, replaceAllMetadata)
                : isImplicitSearchMissingMetadataFallback;
            var loggedMetadataRefreshMode = isImplicitSearchMissingMetadataFallback ? "ImplicitFallback" : metadataRefreshMode;
            var loggedReplaceAllMetadata = isImplicitSearchMissingMetadataFallback ? string.Empty : replaceAllMetadata;
            var itemPath = episodeItem?.Path ?? info.Path ?? string.Empty;
            var originalOverviewSnapshot = ResolveSearchMissingMetadataOverviewCleanupOriginalSnapshot(episodeItem?.Overview, itemPath);
            var episodeId = episodeItem?.Id ?? Guid.Empty;

            if (isSearchMissingMetadataRequest)
            {
                var translationOverviewDiagnostic = EvaluateEpisodeOverviewPersistenceForDiagnostics(
                    translationOverview?.SourceLanguage,
                    translationOverview?.Value,
                    seriesOverview,
                    seasonOverview);
                var detailsOverviewDiagnostic = EvaluateEpisodeOverviewPersistenceForDiagnostics(
                    detailsOverview?.SourceLanguage,
                    detailsOverview?.Value,
                    seriesOverview,
                    seasonOverview);
                var selectedOverviewSource = translationOverviewDiagnostic.Overview != null
                    ? "translation"
                    : detailsOverviewDiagnostic.Overview != null ? "details" : "none";
                this.LogOverviewDiagnosticsInputs(
                    episodeId,
                    itemPath,
                    detailsOverview,
                    translationOverview,
                    seriesOverview,
                    seasonOverview,
                    loggedMetadataRefreshMode,
                    loggedReplaceAllMetadata);
                this.LogOverviewDiagnosticsDecision(
                    episodeId,
                    itemPath,
                    CreateEpisodeLocalizedValue(overviewDecision.Overview, overviewDecision.ResultLanguage),
                    selectedOverviewSource,
                    selectedOverviewSource == "none" ? detailsOverviewDiagnostic.RejectReason : "accepted",
                    translationOverviewDiagnostic.RejectReason,
                    detailsOverviewDiagnostic.RejectReason,
                    loggedMetadataRefreshMode,
                    loggedReplaceAllMetadata);
            }

            if (this.episodeTitleBackfillCoordinator != null)
            {
                this.episodeTitleBackfillCoordinator.Process(new EpisodeTitleBackfillCoordinator.EpisodeTitleBackfillOrchestrationInput(
                    Config.EnableSearchMissingMetadataEpisodeTitleBackfill,
                    episodeId,
                    itemPath,
                    originalMetadataTitle,
                    effectiveProviderTitle,
                    item.Name,
                    isSearchMissingMetadataRequest,
                    loggedMetadataRefreshMode,
                    loggedReplaceAllMetadata,
                    isSearchMissingMetadataRequest,
                    info.MetadataLanguage,
                    titleMetadataLanguage,
                    episodeItem?.PreferredMetadataLanguage,
                    episodeItem?.Series?.PreferredMetadataLanguage,
                    seasonItem?.PreferredMetadataLanguage,
                    titleResolution.DetailsTitle,
                    titleResolution.TranslationTitle));
            }

            if (this.episodeOverviewCleanupCandidateStore != null)
            {
                var shouldEvaluateOverviewCleanup = isSearchMissingMetadataRequest
                    || (!hasExplicitSearchMissingMetadataQuery && string.IsNullOrWhiteSpace(originalOverviewSnapshot));
                var overviewCleanupReason = ResolveSearchMissingMetadataOverviewCleanupReason(
                    shouldEvaluateOverviewCleanup,
                    episodeId,
                    originalOverviewSnapshot,
                    item.Overview);
                if (overviewCleanupReason == SearchMissingMetadataOverviewCleanupReason.CandidateQueued)
                {
                    var nowUtc = DateTimeOffset.UtcNow;
                    this.episodeOverviewCleanupCandidateStore.Save(new EpisodeOverviewCleanupCandidate
                    {
                        ItemId = episodeItem!.Id,
                        ItemPath = itemPath,
                        OriginalOverviewSnapshot = originalOverviewSnapshot,
                        QueuedAtUtc = nowUtc,
                        NextAttemptAtUtc = nowUtc.AddSeconds(10),
                        AttemptCount = 0,
                        ExpiresAtUtc = nowUtc.AddMinutes(2),
                    });
                }
            }

            result.Item = item;

            return result;
        }

        /// <summary>
        /// 重新解析文件名
        /// 注意：这里修改替换 ParentIndexNumber 值后，会重新触发 SeasonProvier 的 GetMetadata 方法，并带上最新的季数 IndexNumber.
        /// </summary>
        /// <returns></returns>
        public EpisodeInfo FixParseInfo(EpisodeInfo info)
        {
            ArgumentNullException.ThrowIfNull(info);

            // 使用 AnitomySharp 进行重新解析，解决 anime 识别错误
            var fileName = Path.GetFileNameWithoutExtension(info.Path) ?? info.Name;
            if (string.IsNullOrEmpty(fileName))
            {
                return info;
            }

            var parseResult = NameParser.ParseEpisode(fileName);
            info.Year = parseResult.Year;
            info.Name = parseResult.ChineseName ?? parseResult.Name;

            // 文件名带有季数数据时，从文件名解析出季数进行修正
            // 修正文件名有特殊命名 SXXEPXX 时，默认解析到错误季数的问题，如神探狄仁杰 Detective.Dee.S01EP01.2006.2160p.WEB-DL.x264.AAC-HQC
            // TODO: 会导致覆盖用户手动修改元数据的季数
            if (parseResult.ParentIndexNumber.HasValue && parseResult.ParentIndexNumber > 0 && info.ParentIndexNumber != parseResult.ParentIndexNumber)
            {
                this.Log("已按 Anitomy 修正季号. old: {0} new: {1}", info.ParentIndexNumber, parseResult.ParentIndexNumber);
                info.ParentIndexNumber = parseResult.ParentIndexNumber;
            }

            // // 修正anime命名格式导致的seasonNumber错误（从season元数据读取)
            // if (info.ParentIndexNumber is null)
            // {
            //     var episodeItem = this._libraryManager.FindByPath(info.Path, false);
            //     var season = episodeItem != null ? ((Episode)episodeItem).Season : null;
            //     if (season != null && season.IndexNumber.HasValue && info.ParentIndexNumber != season.IndexNumber)
            //     {
            //         info.ParentIndexNumber = season.IndexNumber;
            //         this.Log("FixSeasonNumber by season. old: {0} new: {1}", info.ParentIndexNumber, season.IndexNumber);
            //     }
            // }

            // 从季文件夹名称猜出 season number
            // 没有 season 级目录或部分特殊不规范命名，会变成虚拟季，ParentIndexNumber 默认设为 1
            // https://github.com/jellyfin/jellyfin/blob/926470829d91d93b4c0b22c5b8b89a791abbb434/Emby.Server.Implementations/Library/LibraryManager.cs#L2626
            // 从 10.10.7 开始 jellyfin 去掉了虚拟季默认为 1 的处理，需要我们自己修正
            // https://github.com/jellyfin/jellyfin/commit/72911501d34a1da4333f731e1f24169c21248f54
            var isVirtualSeason = this.IsVirtualSeason(info);
            var seasonFolderPath = this.GetOriginalSeasonPath(info);
            if (info.ParentIndexNumber is null or 1 && isVirtualSeason)
            {
                if (seasonFolderPath != null)
                {
                    var guestSeasonNumber = this.GuessSeasonNumberByDirectoryName(seasonFolderPath);
                    if (guestSeasonNumber.HasValue && guestSeasonNumber != info.ParentIndexNumber)
                    {
                        this.Log("已按季目录修正季号. old: {0} new: {1}", info.ParentIndexNumber, guestSeasonNumber);
                        info.ParentIndexNumber = guestSeasonNumber;
                    }
                }
                else
                {
                    this.Log("已按虚拟季修正季号. old: {0} new: {1}", info.ParentIndexNumber, 1);
                    info.ParentIndexNumber = 1;
                }
            }

            // 修正 season number
            // TODO: 10.11有时特殊剧集名如【再与天比高SUPER双语版.E04（国语有删减）.mp4】不传ParentIndexNumber，原因不明
            if (info.ParentIndexNumber is null && !isVirtualSeason && !string.IsNullOrEmpty(seasonFolderPath))
            {
                var guestSeasonNumber = this.LibraryManager.GetSeasonNumberFromPath(seasonFolderPath);
                if (!guestSeasonNumber.HasValue)
                {
                    guestSeasonNumber = this.GuessSeasonNumberByDirectoryName(seasonFolderPath);
                }

                if (guestSeasonNumber.HasValue && guestSeasonNumber != info.ParentIndexNumber)
                {
                    this.Log("已按季目录修正季号. old: {0} new: {1}", info.ParentIndexNumber, guestSeasonNumber);
                    info.ParentIndexNumber = guestSeasonNumber;
                }
            }

            // 识别特典
            if (info.ParentIndexNumber is null && NameParser.IsAnime(fileName) && (parseResult.IsSpecial || NameParser.IsSpecialDirectory(info.Path)))
            {
                this.Log("已将季号修正为特别篇. old: {0} new: 0", info.ParentIndexNumber);
                info.ParentIndexNumber = 0;
            }

            // 特典优先使用文件名（特典除了前面特别设置，还有 SXX/Season XX 等默认的）
            if (info.ParentIndexNumber.HasValue && info.ParentIndexNumber == 0)
            {
                info.Name = parseResult.SpecialName == info.Name ? fileName : parseResult.SpecialName;
            }

            // 修正 episode number
            if (parseResult.IndexNumber.HasValue && info.IndexNumber != parseResult.IndexNumber)
            {
                this.Log("已按 Anitomy 修正集号. old: {0} new: {1}", info.IndexNumber, parseResult.IndexNumber);
                info.IndexNumber = parseResult.IndexNumber;
            }

            return info;
        }

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        internal static string? ResolveEpisodeTitlePersistence(string? originalMetadataTitle, EpisodeLocalizedValue? providerTitle)
        {
            return Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill.EpisodeTitleBackfillPolicy.ResolveEpisodeTitlePersistence(originalMetadataTitle, providerTitle);
        }

        internal static bool IsGenericTmdbEpisodeTitle(string? title)
        {
            return Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill.EpisodeTitleBackfillPolicy.IsGenericTmdbEpisodeTitle(title);
        }

        internal static bool IsDefaultJellyfinEpisodeTitle(string? title)
        {
            return Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill.EpisodeTitleBackfillPolicy.IsDefaultJellyfinEpisodeTitle(title);
        }

        internal static bool ShouldQueueSearchMissingMetadataOverviewCleanup(Guid itemId, string? originalOverview, string? resolvedOverview)
        {
            return ResolveSearchMissingMetadataOverviewCleanupReason(
                true,
                itemId,
                originalOverview,
                resolvedOverview) == SearchMissingMetadataOverviewCleanupReason.CandidateQueued;
        }

        internal static (string? Overview, string? ResultLanguage) ResolveEpisodeOverviewPersistence(string? overviewSourceLanguage, string? overview, string? seriesOverview, string? seasonOverview)
        {
            if (string.IsNullOrWhiteSpace(overview))
            {
                return (null, null);
            }

            var trimmedOriginalOverview = overview.Trim();
            var normalizedOverviewSourceLanguage = ResolveEpisodeOverviewSourceLanguage(overviewSourceLanguage);
            if (normalizedOverviewSourceLanguage == null)
            {
                return (null, null);
            }

            var normalizedOverview = NormalizeOverviewForComparison(trimmedOriginalOverview);
            var normalizedSeriesOverview = NormalizeOverviewForComparison(seriesOverview);
            var normalizedSeasonOverview = NormalizeOverviewForComparison(seasonOverview);

            if ((normalizedOverview != null && IsOverviewTooSimilarToParent(normalizedOverview, normalizedSeriesOverview))
                || (normalizedOverview != null && IsOverviewTooSimilarToParent(normalizedOverview, normalizedSeasonOverview)))
            {
                return (null, null);
            }

            return (trimmedOriginalOverview, normalizedOverviewSourceLanguage);
        }

        internal static SearchMissingMetadataOverviewCleanupReason ResolveSearchMissingMetadataOverviewCleanupReason(bool isSearchMissingMetadataRequest, Guid itemId, string? originalOverview, string? resolvedOverview)
        {
            if (!isSearchMissingMetadataRequest)
            {
                return SearchMissingMetadataOverviewCleanupReason.RequestNotSearchMissingMetadata;
            }

            if (itemId == Guid.Empty)
            {
                return SearchMissingMetadataOverviewCleanupReason.EpisodeIdMissing;
            }

            if (!string.IsNullOrWhiteSpace(resolvedOverview))
            {
                return SearchMissingMetadataOverviewCleanupReason.TrustedOverviewPresent;
            }

            if (!string.IsNullOrWhiteSpace(originalOverview))
            {
                return SearchMissingMetadataOverviewCleanupReason.OriginalOverviewSnapshotPresent;
            }

            return SearchMissingMetadataOverviewCleanupReason.CandidateQueued;
        }

        internal static string ResolveSearchMissingMetadataOverviewCleanupOriginalSnapshot(string? originalOverview, string? itemPath)
        {
            var trimmedOriginalOverview = (originalOverview ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmedOriginalOverview))
            {
                return string.Empty;
            }

            var persistedNfoPlot = TryReadEpisodeNfoPlot(itemPath);
            if (persistedNfoPlot != null && string.IsNullOrWhiteSpace(persistedNfoPlot))
            {
                return string.Empty;
            }

            return trimmedOriginalOverview;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.memoryCache.Dispose();
            }
        }

        protected int GetVideoFileCount(string? dir)
        {
            if (dir == null)
            {
                return 0;
            }

            var cacheKey = $"filecount_{dir}";
            if (this.memoryCache.TryGetValue<int>(cacheKey, out var videoFilesCount))
            {
                return videoFilesCount;
            }

            var dirInfo = new DirectoryInfo(dir);

            var files = dirInfo.GetFiles();
            var nameOptions = new Emby.Naming.Common.NamingOptions();

            foreach (var fileInfo in files.Where(f => !f.Attributes.HasFlag(FileAttributes.Hidden)))
            {
                if (Emby.Naming.Video.VideoResolver.IsVideoFile(fileInfo.FullName, nameOptions))
                {
                    videoFilesCount++;
                }
            }

            var expiredOption = new MemoryCacheEntryOptions() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) };
            this.memoryCache.Set<int>(cacheKey, videoFilesCount, expiredOption);
            return videoFilesCount;
        }

        private static string? TryReadEpisodeNfoPlot(string? itemPath)
        {
            if (string.IsNullOrWhiteSpace(itemPath))
            {
                return null;
            }

            var nfoPath = Path.ChangeExtension(itemPath, ".nfo");
            if (!File.Exists(nfoPath))
            {
                return null;
            }

            try
            {
                var document = XDocument.Load(nfoPath);
                return (document.Root?.Element("plot")?.Value ?? string.Empty).Trim();
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (XmlException)
            {
                return null;
            }
        }

        private static string? ResolveEpisodeTargetMetadataLanguage(string? requestedLanguage)
        {
            var normalizedRequestedLanguage = string.IsNullOrWhiteSpace(requestedLanguage)
                ? null
                : ChineseLocalePolicy.CanonicalizeLanguage(requestedLanguage);
            if (string.IsNullOrWhiteSpace(normalizedRequestedLanguage))
            {
                return null;
            }

            return ChineseLocalePolicy.IsChineseRequest(normalizedRequestedLanguage)
                ? "zh-CN"
                : normalizedRequestedLanguage;
        }

        private static string? ResolveEpisodeOverviewSourceLanguage(string? sourceLanguage)
        {
            var normalizedSourceLanguage = string.IsNullOrWhiteSpace(sourceLanguage)
                ? null
                : ChineseLocalePolicy.CanonicalizeLanguage(sourceLanguage);

            return string.Equals(normalizedSourceLanguage, "zh-CN", StringComparison.OrdinalIgnoreCase)
                ? "zh-CN"
                : null;
        }

        private static EpisodeLocalizedValue? CreateEpisodeLocalizedValue(string? value, string? sourceLanguage)
        {
            if (value == null && sourceLanguage == null)
            {
                return null;
            }

            return new EpisodeLocalizedValue
            {
                Value = value,
                SourceLanguage = sourceLanguage,
            };
        }

        private static EpisodeLocalizedValue? TrimEpisodeLocalizedValue(EpisodeLocalizedValue? value)
        {
            if (value == null)
            {
                return null;
            }

            return CreateEpisodeLocalizedValue(value.Value?.Trim(), value.SourceLanguage);
        }

        private static (string State, int Length, string Hash, string SourceLanguage, string ScriptKind) DescribeOverviewText(string? value, string? sourceLanguage)
        {
            var trimmedValue = value?.Trim();
            var normalizedSourceLanguage = string.IsNullOrWhiteSpace(sourceLanguage)
                ? string.Empty
                : ChineseLocalePolicy.CanonicalizeLanguage(sourceLanguage) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmedValue))
            {
                return ("empty", 0, string.Empty, normalizedSourceLanguage, "unknown");
            }

            return ("non-empty", trimmedValue.Length, ComputeOverviewHash(trimmedValue), normalizedSourceLanguage, ResolveOverviewScriptKind(trimmedValue));
        }

        private static string ComputeOverviewHash(string value)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }

        private static (string? Overview, string? ResultLanguage, string RejectReason) EvaluateEpisodeOverviewPersistenceForDiagnostics(string? overviewSourceLanguage, string? overview, string? seriesOverview, string? seasonOverview)
        {
            if (string.IsNullOrWhiteSpace(overview))
            {
                return (null, null, "empty");
            }

            var trimmedOriginalOverview = overview.Trim();
            var normalizedOverviewSourceLanguage = ResolveEpisodeOverviewSourceLanguage(overviewSourceLanguage);
            if (normalizedOverviewSourceLanguage == null)
            {
                return (null, null, "source-not-zh-cn");
            }

            var normalizedOverview = NormalizeOverviewForComparison(trimmedOriginalOverview);
            var normalizedSeriesOverview = NormalizeOverviewForComparison(seriesOverview);
            if (normalizedOverview != null && IsOverviewTooSimilarToParent(normalizedOverview, normalizedSeriesOverview))
            {
                return (null, null, "duplicate-series");
            }

            var normalizedSeasonOverview = NormalizeOverviewForComparison(seasonOverview);
            if (normalizedOverview != null && IsOverviewTooSimilarToParent(normalizedOverview, normalizedSeasonOverview))
            {
                return (null, null, "duplicate-season");
            }

            return (trimmedOriginalOverview, normalizedOverviewSourceLanguage, "accepted");
        }

        private static string ResolveOverviewScriptKind(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || !value.HasChinese())
            {
                return "unknown";
            }

            var hasSimplified = false;
            var hasTraditional = false;
            foreach (var character in value)
            {
                if (SimplifiedOverviewScriptDistinctiveCharacters.Contains(character))
                {
                    hasSimplified = true;
                }

                if (TraditionalOverviewScriptDistinctiveCharacters.Contains(character))
                {
                    hasTraditional = true;
                }

                if (hasSimplified && hasTraditional)
                {
                    return "mixed";
                }
            }

            if (hasSimplified)
            {
                return "simplified";
            }

            return hasTraditional ? "traditional" : "unknown";
        }

        private async Task<(EpisodeLocalizedValue? DetailsTitle, EpisodeLocalizedValue? TranslationTitle, EpisodeLocalizedValue? EffectiveProviderTitle)> ResolveEffectiveEpisodeProviderTitleAsync(int seriesTmdbId, int? seasonNumber, int? episodeNumber, string displayOrder, string? titleMetadataLanguage, string? imageLanguages, string? providerTitle, CancellationToken cancellationToken)
        {
            var normalizedTitleMetadataLanguage = string.IsNullOrWhiteSpace(titleMetadataLanguage) ? null : ChineseLocalePolicy.CanonicalizeLanguage(titleMetadataLanguage);
            var detailsTitle = TrimEpisodeLocalizedValue(CreateEpisodeLocalizedValue(
                providerTitle,
                string.Equals(normalizedTitleMetadataLanguage, "zh-CN", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : null));
            var trimmedProviderTitle = detailsTitle?.Value;
            if (!ChineseLocalePolicy.IsAllowedForStrictZhCn(normalizedTitleMetadataLanguage)
                || !IsGenericTmdbEpisodeTitle(trimmedProviderTitle))
            {
                return (detailsTitle, null, detailsTitle);
            }

            var translationTitle = TrimEpisodeLocalizedValue(await this.GetEpisodeTranslationTitleAsync(
                    seriesTmdbId,
                    seasonNumber,
                    episodeNumber,
                    displayOrder,
                    normalizedTitleMetadataLanguage,
                    imageLanguages,
                    cancellationToken)
                .ConfigureAwait(false));
            var trimmedTranslationTitle = translationTitle?.Value;
            if (string.IsNullOrWhiteSpace(trimmedTranslationTitle)
                || IsGenericTmdbEpisodeTitle(trimmedTranslationTitle))
            {
                return (detailsTitle, translationTitle, detailsTitle);
            }

            return (detailsTitle, translationTitle, translationTitle);
        }

#pragma warning disable CA1848
        private void LogOverviewDiagnosticsInputs(Guid itemId, string itemPath, EpisodeLocalizedValue? detailsOverview, EpisodeLocalizedValue? translationOverview, string? seriesOverview, string? seasonOverview, string? metadataRefreshMode, string? replaceAllMetadata)
        {
            var details = DescribeOverviewText(detailsOverview?.Value, detailsOverview?.SourceLanguage);
            var translation = DescribeOverviewText(translationOverview?.Value, translationOverview?.SourceLanguage);
            var series = DescribeOverviewText(seriesOverview, null);
            var season = DescribeOverviewText(seasonOverview, null);

            this.Logger.LogDebug(
                "[MetaShark] 剧集简介诊断输入. itemId={ItemId} itemPath={ItemPath} detailsOverviewState={DetailsOverviewState} detailsOverviewLength={DetailsOverviewLength} detailsOverviewHash={DetailsOverviewHash} detailsOverviewSourceLanguage={DetailsOverviewSourceLanguage} detailsOverviewScriptKind={DetailsOverviewScriptKind} translationOverviewState={TranslationOverviewState} translationOverviewLength={TranslationOverviewLength} translationOverviewHash={TranslationOverviewHash} translationOverviewSourceLanguage={TranslationOverviewSourceLanguage} translationOverviewScriptKind={TranslationOverviewScriptKind} seriesOverviewState={SeriesOverviewState} seriesOverviewLength={SeriesOverviewLength} seriesOverviewHash={SeriesOverviewHash} seriesOverviewSourceLanguage={SeriesOverviewSourceLanguage} seriesOverviewScriptKind={SeriesOverviewScriptKind} seasonOverviewState={SeasonOverviewState} seasonOverviewLength={SeasonOverviewLength} seasonOverviewHash={SeasonOverviewHash} seasonOverviewSourceLanguage={SeasonOverviewSourceLanguage} seasonOverviewScriptKind={SeasonOverviewScriptKind} metadataRefreshMode={MetadataRefreshMode} replaceAllMetadata={ReplaceAllMetadata}.",
                itemId,
                itemPath,
                details.State,
                details.Length,
                details.Hash,
                details.SourceLanguage,
                details.ScriptKind,
                translation.State,
                translation.Length,
                translation.Hash,
                translation.SourceLanguage,
                translation.ScriptKind,
                series.State,
                series.Length,
                series.Hash,
                series.SourceLanguage,
                series.ScriptKind,
                season.State,
                season.Length,
                season.Hash,
                season.SourceLanguage,
                season.ScriptKind,
                metadataRefreshMode ?? string.Empty,
                replaceAllMetadata ?? string.Empty);
        }

        private void LogOverviewDiagnosticsDecision(Guid itemId, string itemPath, EpisodeLocalizedValue? selectedOverview, string selectedOverviewSource, string rejectReason, string translationRejectReason, string detailsRejectReason, string? metadataRefreshMode, string? replaceAllMetadata)
        {
            var selected = DescribeOverviewText(selectedOverview?.Value, selectedOverview?.SourceLanguage);

            this.Logger.LogDebug(
                "[MetaShark] 剧集简介诊断决策. itemId={ItemId} itemPath={ItemPath} selectedOverviewState={SelectedOverviewState} selectedOverviewLength={SelectedOverviewLength} selectedOverviewHash={SelectedOverviewHash} selectedOverviewSourceLanguage={SelectedOverviewSourceLanguage} selectedOverviewScriptKind={SelectedOverviewScriptKind} selectedOverviewSource={SelectedOverviewSource} rejectReason={RejectReason} translationRejectReason={TranslationRejectReason} detailsRejectReason={DetailsRejectReason} metadataRefreshMode={MetadataRefreshMode} replaceAllMetadata={ReplaceAllMetadata}.",
                itemId,
                itemPath,
                selected.State,
                selected.Length,
                selected.Hash,
                selected.SourceLanguage,
                selected.ScriptKind,
                selectedOverviewSource,
                rejectReason,
                translationRejectReason,
                detailsRejectReason,
                metadataRefreshMode ?? string.Empty,
                replaceAllMetadata ?? string.Empty);
        }

#pragma warning restore CA1848

#pragma warning disable SA1204
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Overview comparison contract requires lowercase normalization.")]
        private static string? NormalizeOverviewForComparison(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmedValue = value.Trim()
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\t', ' ');
            var builder = new StringBuilder(trimmedValue.Length);
            var previousWasWhitespace = false;

            foreach (var character in trimmedValue)
            {
                if (char.IsWhiteSpace(character))
                {
                    if (!previousWasWhitespace)
                    {
                        builder.Append(' ');
                        previousWasWhitespace = true;
                    }

                    continue;
                }

                builder.Append(character);
                previousWasWhitespace = false;
            }

            return builder.ToString().ToLowerInvariant();
        }

        private static bool IsOverviewTooSimilarToParent(string normalizedOverview, string? normalizedParentOverview)
        {
            if (normalizedOverview == null || normalizedParentOverview == null)
            {
                return false;
            }

            if (normalizedOverview == normalizedParentOverview)
            {
                return true;
            }

            return normalizedOverview.Distance(normalizedParentOverview) >= 0.95;
        }

#pragma warning restore SA1204

        private string? GetOriginalSeriesPath(EpisodeInfo info)
        {
            ArgumentNullException.ThrowIfNull(info);
            if (info.Path == null)
            {
                return null;
            }

            var seasonPath = Path.GetDirectoryName(info.Path);
            if (string.IsNullOrEmpty(seasonPath))
            {
                return null;
            }

            var item = this.LibraryManager.FindByPath(seasonPath, true);

            if (item is Series)
            {
                return seasonPath;
            }

            var seriesPath = Path.GetDirectoryName(seasonPath);
            if (string.IsNullOrEmpty(seriesPath))
            {
                return null;
            }

            return this.LibraryManager.FindByPath(seriesPath, true) is Series ? seriesPath : null;
        }

        private async Task<TvdbSpecialPlacement?> TryBuildTvdbSpecialPlacementAsync(
            string seriesTvdbId,
            int? episodeNumber,
            string? metadataLanguage,
            CancellationToken cancellationToken)
        {
            if (!int.TryParse(seriesTvdbId, out var seriesId) || episodeNumber is null or 0)
            {
                LogTvdbPlacementInvalidInput(this.Logger, seriesTvdbId, episodeNumber ?? 0, null);
                return null;
            }

            var episodes = await this.tvdbApi
                .GetSeriesEpisodesAsync(seriesId, "official", 0, metadataLanguage, cancellationToken)
                .ConfigureAwait(false);
            if (episodes.Count == 0)
            {
                LogTvdbPlacementEmptyList(this.Logger, seriesTvdbId, null);
                return null;
            }

            var match = episodes.FirstOrDefault(e => e.SeasonNumber == 0 && e.Number == episodeNumber);
            if (match == null)
            {
                LogTvdbPlacementNoMatch(this.Logger, seriesTvdbId, episodeNumber ?? 0, null);
                return null;
            }

            return new TvdbSpecialPlacement
            {
                AirsBeforeSeasonNumber = match.AirsBeforeSeason,
                AirsBeforeEpisodeNumber = match.AirsBeforeEpisode,
                AirsAfterSeasonNumber = match.AirsAfterSeason,
            };
        }

        private async Task<string?> ResolveSeriesTvdbIdAsync(EpisodeInfo info, string? seriesTmdbId, CancellationToken cancellationToken)
        {
            if (info.SeriesProviderIds.TryGetValue(MetadataProvider.Tvdb.ToString(), out var seriesTvdbId)
                && !string.IsNullOrWhiteSpace(seriesTvdbId))
            {
                LogTvdbIdResolved(this.Logger, seriesTvdbId, null);
                return seriesTvdbId;
            }

            var episodeItem = this.LibraryManager.FindByPath(info.Path, false) as Episode;
            var seriesItem = episodeItem?.Series;
            seriesTvdbId = seriesItem?.GetProviderId(MetadataProvider.Tvdb);
            if (!string.IsNullOrWhiteSpace(seriesTvdbId))
            {
                info.SeriesProviderIds[MetadataProvider.Tvdb.ToString()] = seriesTvdbId;
                LogTvdbIdResolved(this.Logger, seriesTvdbId, null);
                return seriesTvdbId;
            }

            if (!string.IsNullOrWhiteSpace(seriesTmdbId) && int.TryParse(seriesTmdbId, out var tmdbId))
            {
                var series = await this.TmdbApi
                    .GetSeriesAsync(tmdbId, info.MetadataLanguage ?? string.Empty, info.MetadataLanguage ?? string.Empty, cancellationToken)
                    .ConfigureAwait(false);
                seriesTvdbId = series?.ExternalIds?.TvdbId;
                if (!string.IsNullOrWhiteSpace(seriesTvdbId))
                {
                    info.SeriesProviderIds[MetadataProvider.Tvdb.ToString()] = seriesTvdbId;
                    LogTvdbIdResolved(this.Logger, seriesTvdbId, null);
                    return seriesTvdbId;
                }
            }

            LogTvdbIdMissing(this.Logger, "EpisodeInfo/SeriesItem/TmdbExternalIds", null);
            return null;
        }

        private MetadataResult<Episode>? HandleAnimeExtras(EpisodeInfo info)
        {
            // 特典或extra视频可能和正片剧集放在同一目录
            var fileName = Path.GetFileNameWithoutExtension(info.Path) ?? info.Name;
            if (string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            var parseResult = NameParser.ParseEpisode(fileName);
            if (parseResult.IsExtra)
            {
                this.Log("识别到动画特典，跳过常规单集处理. name: {0}", fileName);
                var result = new MetadataResult<Episode>();
                result.HasMetadata = true;

                // 假如已有ParentIndexNumber，设为特典覆盖掉（设为null不会替换旧值）
                if (info.ParentIndexNumber.HasValue)
                {
                    result.Item = new Episode
                    {
                        ParentIndexNumber = 0,
                        IndexNumber = null,
                        Name = parseResult.ExtraName,
                    };
                    return result;
                }

                // 没ParentIndexNumber时只修改名称
                result.Item = new Episode
                {
                    Name = parseResult.ExtraName,
                };
                return result;
            }

            //// 特典也有 tmdb 剧集信息，不在这里处理
            // if (parseResult.IsSpecial || NameParser.IsSpecialDirectory(info.Path))
            // {
            //     this.Log($"Found anime sp of [name]: {fileName}");
            //     var result = new MetadataResult<Episode>();
            //     result.HasMetadata = true;
            //     result.Item = new Episode
            //     {
            //         ParentIndexNumber = 0,
            //         IndexNumber = parseResult.IndexNumber,
            //         Name = parseResult.SpecialName == info.Name ? fileName : parseResult.SpecialName,
            //     };

            // return result;
            // }
            return null;
        }

        private sealed class TvdbSpecialPlacement
        {
            public int? AirsBeforeSeasonNumber { get; set; }

            public int? AirsBeforeEpisodeNumber { get; set; }

            public int? AirsAfterSeasonNumber { get; set; }
        }
    }
}
