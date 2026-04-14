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
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Plugin.MetaShark.Api;
    using Jellyfin.Plugin.MetaShark.Core;
    using Jellyfin.Plugin.MetaShark.Model;
    using Jellyfin.Plugin.MetaShark.Workers;
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
            LoggerMessage.Define<string, int, int, string>(LogLevel.Debug, new EventId(2, nameof(LogTvdbPlacementLookup)), "TVDB special placement lookup. tvdbId={TvdbId} s{Season}e{Episode} lang={Lang}");

        private static readonly Action<ILogger, string, int, int, Exception?> LogTvdbPlacementNotFound =
            LoggerMessage.Define<string, int, int>(LogLevel.Debug, new EventId(3, nameof(LogTvdbPlacementNotFound)), "TVDB special placement not found. tvdbId={TvdbId} s{Season}e{Episode}");

        private static readonly Action<ILogger, string, int, Exception?> LogTvdbPlacementInvalidInput =
            LoggerMessage.Define<string, int>(LogLevel.Debug, new EventId(4, nameof(LogTvdbPlacementInvalidInput)), "TVDB placement invalid input. tvdbId={TvdbId} episode={Episode}");

        private static readonly Action<ILogger, string, Exception?> LogTvdbPlacementEmptyList =
            LoggerMessage.Define<string>(LogLevel.Debug, new EventId(5, nameof(LogTvdbPlacementEmptyList)), "TVDB placement empty episode list. tvdbId={TvdbId}");

        private static readonly Action<ILogger, string, int, Exception?> LogTvdbPlacementNoMatch =
            LoggerMessage.Define<string, int>(LogLevel.Debug, new EventId(6, nameof(LogTvdbPlacementNoMatch)), "TVDB placement no match. tvdbId={TvdbId} episode={Episode}");

        private static readonly Action<ILogger, string, Exception?> LogTvdbIdMissing =
            LoggerMessage.Define<string>(LogLevel.Debug, new EventId(7, nameof(LogTvdbIdMissing)), "TVDB id not found for series. source={Source}");

        private static readonly Action<ILogger, string, Exception?> LogTvdbIdResolved =
            LoggerMessage.Define<string>(LogLevel.Debug, new EventId(8, nameof(LogTvdbIdResolved)), "TVDB id resolved: {TvdbId}");

        private static readonly Action<ILogger, Guid, string, string, string, string, Exception?> LogQueuedSearchMissingMetadataTitleBackfill =
            LoggerMessage.Define<Guid, string, string, string, string>(
                LogLevel.Debug,
                new EventId(9, nameof(LogQueuedSearchMissingMetadataTitleBackfill)),
                "Queued episode title backfill candidate for {ItemId}. originalTitle={OriginalTitle} candidateTitle={CandidateTitle} metadataRefreshMode={MetadataRefreshMode} replaceAllMetadata={ReplaceAllMetadata}.");

        private readonly MemoryCache memoryCache;
        private readonly IEpisodeTitleBackfillCandidateStore? episodeTitleBackfillCandidateStore;
        private readonly TvdbApi tvdbApi;

        public EpisodeProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, DoubanApi doubanApi, TmdbApi tmdbApi, OmdbApi omdbApi, ImdbApi imdbApi, TvdbApi tvdbApi, IEpisodeTitleBackfillCandidateStore? episodeTitleBackfillCandidateStore = null)
            : base(httpClientFactory, loggerFactory.CreateLogger<EpisodeProvider>(), libraryManager, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi)
        {
            this.memoryCache = new MemoryCache(new MemoryCacheOptions());
            this.episodeTitleBackfillCandidateStore = episodeTitleBackfillCandidateStore;
            this.tvdbApi = tvdbApi;
        }

        public string Name => MetaSharkPlugin.PluginName;

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(EpisodeInfo searchInfo, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(searchInfo);
            this.Log($"GetEpisodeSearchResults of [name]: {searchInfo.Name}");
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
            this.Log($"GetEpisodeMetadata of [name]: {info.Name} [fileName]: {fileName} number: {info.IndexNumber} ParentIndexNumber: {info.ParentIndexNumber} IsMissingEpisode: {info.IsMissingEpisode} EnableTmdb: {Config.EnableTmdb} DisplayOrder: {info.SeriesDisplayOrder}");
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
                this.Log("缺少元数据. episodeNumber: {0} seasonNumber: {1} seriesTmdbId:{2}", episodeNumber, seasonNumber, seriesTmdbId);
                return result;
            }

            var episodeResult = await this.GetEpisodeAsync(
                    seriesTmdbId.ToInt(),
                    seasonNumber,
                    episodeNumber,
                    info.SeriesDisplayOrder,
                    info.MetadataLanguage,
                    info.MetadataLanguage,
                    cancellationToken)
                .ConfigureAwait(false);
            if (episodeResult == null)
            {
                this.Log("找不到tmdb剧集数据. seriesTmdbId: {0} seasonNumber: {1} episodeNumber: {2} displayOrder: {3}", seriesTmdbId, seasonNumber, episodeNumber, info.SeriesDisplayOrder);
                return result;
            }

            result.HasMetadata = true;
            result.QueriedById = true;

            var episodeItem = this.LibraryManager.FindByPath(info.Path, false) as Episode;
            var seriesOverview = episodeItem?.Series?.Overview;
            var seasonPath = this.GetOriginalSeasonPath(info);
            var seasonItem = !string.IsNullOrWhiteSpace(seasonPath) ? this.LibraryManager.FindByPath(seasonPath, true) as Season : null;
            var seasonOverview = seasonItem?.Overview;
            var overviewDecision = ResolveEpisodeOverviewPersistence(info.MetadataLanguage, episodeResult.Overview, seriesOverview, seasonOverview);

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
                            "TVDB special placement result. tvdbId: {0} s{1}e{2} -> beforeSeason: {3} beforeEpisode: {4} afterSeason: {5}",
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
                    this.Log("TVDB special placement skipped (missing tvdb id). s{0}e{1}", seasonNumber, episodeNumber);
                }
            }

            item.PremiereDate = episodeResult.AirDate;
            item.ProductionYear = episodeResult.AirDate?.Year;
            item.Name = ResolveEpisodeTitlePersistence(info.MetadataLanguage, originalMetadataTitle, episodeResult.Name);
            item.CommunityRating = (float)System.Math.Round(episodeResult.VoteAverage, 1);

            var metadataRefreshMode = this.HttpContextAccessor.HttpContext?.Request.Query["metadataRefreshMode"].ToString();
            var replaceAllMetadata = this.HttpContextAccessor.HttpContext?.Request.Query["replaceAllMetadata"].ToString();
            if (this.episodeTitleBackfillCandidateStore != null
                && IsSearchMissingMetadataRefresh(metadataRefreshMode, replaceAllMetadata)
                && ShouldQueueSearchMissingMetadataTitleBackfill(Config.EnableSearchMissingMetadataEpisodeTitleBackfill, episodeItem?.Id ?? Guid.Empty, originalMetadataTitle, item.Name))
            {
                var nowUtc = DateTimeOffset.UtcNow;
                var originalTitleSnapshot = (originalMetadataTitle ?? string.Empty).Trim();
                var candidateTitle = (item.Name ?? string.Empty).Trim();
                this.episodeTitleBackfillCandidateStore.Save(new EpisodeTitleBackfillCandidate
                {
                    ItemId = episodeItem!.Id,
                    OriginalTitleSnapshot = originalTitleSnapshot,
                    CandidateTitle = candidateTitle,
                    CreatedAtUtc = nowUtc,
                    ExpiresAtUtc = nowUtc.AddMinutes(10),
                });
                LogQueuedSearchMissingMetadataTitleBackfill(
                    this.Logger,
                    episodeItem.Id,
                    originalTitleSnapshot,
                    candidateTitle,
                    metadataRefreshMode ?? string.Empty,
                    replaceAllMetadata ?? string.Empty,
                    null);
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
                this.Log("FixSeasonNumber by anitomy. old: {0} new: {1}", info.ParentIndexNumber, parseResult.ParentIndexNumber);
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
                        this.Log("FixSeasonNumber by season path. old: {0} new: {1}", info.ParentIndexNumber, guestSeasonNumber);
                        info.ParentIndexNumber = guestSeasonNumber;
                    }
                }
                else
                {
                    this.Log("FixSeasonNumber by virtual season. old: {0} new: {1}", info.ParentIndexNumber, 1);
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
                    this.Log("FixSeasonNumber by season path. old: {0} new: {1}", info.ParentIndexNumber, guestSeasonNumber);
                    info.ParentIndexNumber = guestSeasonNumber;
                }
            }

            // 识别特典
            if (info.ParentIndexNumber is null && NameParser.IsAnime(fileName) && (parseResult.IsSpecial || NameParser.IsSpecialDirectory(info.Path)))
            {
                this.Log("FixSeasonNumber to special. old: {0} new: 0", info.ParentIndexNumber);
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
                this.Log("FixEpisodeNumber by anitomy. old: {0} new: {1}", info.IndexNumber, parseResult.IndexNumber);
                info.IndexNumber = parseResult.IndexNumber;
            }

            return info;
        }

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        internal static string? ResolveEpisodeTitlePersistence(string? metadataLanguage, string? originalMetadataTitle, string? providerTitle)
        {
            if (!IsDefaultJellyfinEpisodeTitle(originalMetadataTitle))
            {
                return providerTitle;
            }

            var normalizedMetadataLanguage = string.IsNullOrWhiteSpace(metadataLanguage) ? null : ChineseLocalePolicy.CanonicalizeLanguage(metadataLanguage);
            if (!ChineseLocalePolicy.IsAllowedForStrictZhCn(normalizedMetadataLanguage))
            {
                return originalMetadataTitle;
            }

            var trimmedProviderTitle = providerTitle?.Trim();
            if (string.IsNullOrWhiteSpace(trimmedProviderTitle))
            {
                return originalMetadataTitle;
            }

            if (IsDefaultJellyfinEpisodeTitle(trimmedProviderTitle))
            {
                return originalMetadataTitle;
            }

            if (!ChineseLocalePolicy.IsTextAllowedForStrictZhCn(trimmedProviderTitle))
            {
                return originalMetadataTitle;
            }

            return trimmedProviderTitle;
        }

        internal static bool IsDefaultJellyfinEpisodeTitle(string? title)
        {
            if (string.IsNullOrEmpty(title) || !title.StartsWith("第 ", StringComparison.Ordinal) || !title.EndsWith(" 集", StringComparison.Ordinal))
            {
                return false;
            }

            var numericPart = title.Substring(2, title.Length - 4);
            if (numericPart.Length == 0 || numericPart[0] == '0')
            {
                return false;
            }

            foreach (var character in numericPart)
            {
                if (!char.IsDigit(character))
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool IsSearchMissingMetadataRefresh(string? metadataRefreshMode, string? replaceAllMetadata)
        {
            return string.Equals(metadataRefreshMode?.Trim(), "FullRefresh", StringComparison.OrdinalIgnoreCase)
                && string.Equals(replaceAllMetadata?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool ShouldQueueSearchMissingMetadataTitleBackfill(bool featureEnabled, Guid itemId, string? originalMetadataTitle, string? resolvedTitle)
        {
            if (!featureEnabled || itemId == Guid.Empty)
            {
                return false;
            }

            var trimmedOriginalTitle = originalMetadataTitle?.Trim();
            if (!IsDefaultJellyfinEpisodeTitle(trimmedOriginalTitle))
            {
                return false;
            }

            var trimmedResolvedTitle = resolvedTitle?.Trim();
            if (string.IsNullOrWhiteSpace(trimmedResolvedTitle))
            {
                return false;
            }

            return !string.Equals(trimmedOriginalTitle, trimmedResolvedTitle, StringComparison.Ordinal);
        }

        internal static (string? Overview, string? ResultLanguage) ResolveEpisodeOverviewPersistence(string? metadataLanguage, string? overview, string? seriesOverview, string? seasonOverview)
        {
            if (string.IsNullOrWhiteSpace(overview))
            {
                return (null, null);
            }

            var trimmedOriginalOverview = overview.Trim();
            var normalizedMetadataLanguage = string.IsNullOrWhiteSpace(metadataLanguage) ? null : ChineseLocalePolicy.CanonicalizeLanguage(metadataLanguage);
            var isChineseRequest = ChineseLocalePolicy.IsChineseRequest(normalizedMetadataLanguage);

            if (isChineseRequest && !trimmedOriginalOverview.HasChinese())
            {
                return (null, null);
            }

            if (ChineseLocalePolicy.IsAllowedForStrictZhCn(normalizedMetadataLanguage)
                && !ChineseLocalePolicy.IsTextAllowedForStrictZhCn(trimmedOriginalOverview))
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

            return (trimmedOriginalOverview, normalizedMetadataLanguage);
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
                this.Log($"Found anime extra of [name]: {fileName}");
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
