// <copyright file="SeasonProvider.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Data.Enums;
    using Jellyfin.Plugin.MetaShark.Api;
    using Jellyfin.Plugin.MetaShark.Core;
    using Jellyfin.Plugin.MetaShark.Model;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Providers;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;

    public class SeasonProvider : BaseProvider, IRemoteMetadataProvider<Season, SeasonInfo>
    {
        public SeasonProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, DoubanApi doubanApi, TmdbApi tmdbApi, OmdbApi omdbApi, ImdbApi imdbApi)
            : base(httpClientFactory, loggerFactory.CreateLogger<SeasonProvider>(), libraryManager, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi)
        {
        }

        public string Name => MetaSharkPlugin.PluginName;

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeasonInfo searchInfo, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(searchInfo);
            this.Log("开始搜索季候选. name: {0}", searchInfo.Name);
            return await Task.FromResult(Enumerable.Empty<RemoteSearchResult>()).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<MetadataResult<Season>> GetMetadata(SeasonInfo info, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(info);
            var result = new MetadataResult<Season>();
            var semantic = this.ResolveMetadataSemantic(info);
            var doubanAllowed = IsDoubanAllowed(semantic);

            // 使用刷新元数据时，之前识别的 seasonNumber 会保留，不会被覆盖
            info.SeriesProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var seriesTmdbId);
            info.SeriesProviderIds.TryGetMetaSource(MetaSharkPlugin.ProviderId, out var metaSource);
            info.SeriesProviderIds.TryGetValue(DoubanProviderId, out var sid);
            var seasonNumber = info.IndexNumber; // S00/Season 00特典目录会为0
            var seasonSid = info.GetProviderId(DoubanProviderId);
            var fileName = Path.GetFileName(info.Path);
            this.Log("开始获取季元数据. name: {0} fileName: {1} seasonNumber: {2} seriesTmdbId: {3} sid: {4} metaSource: {5} enableTmdb: {6}", info.Name, fileName, info.IndexNumber, seriesTmdbId, sid, metaSource, Config.EnableTmdb);
            if (!doubanAllowed)
            {
                if (!string.IsNullOrWhiteSpace(seriesTmdbId))
                {
                    return await this.GetMetadataByTmdb(info, seriesTmdbId, seasonNumber, cancellationToken).ConfigureAwait(false);
                }

                return result;
            }

            if (metaSource != MetaSource.Tmdb && !string.IsNullOrEmpty(sid))
            {
                // seasonNumber 为 null 有三种情况：
                // 1. 没有季文件夹时，即虚拟季，info.Path 为空
                // 2. 一般不规范文件夹命名，没法被 EpisodeResolver 解析的，info.Path 不为空，如：摇曳露营△
                // 3. 特殊不规范文件夹命名，能被 EpisodeResolver 错误解析，这时被当成了视频文件，相当于没有季文件夹，info.Path 为空，如：冰与火之歌 S02.列王的纷争.2012.1080p.Blu-ray.x265.10bit.AC3
                //    相关代码：https://github.com/jellyfin/jellyfin/blob/dc2eca9f2ca259b46c7b53f59251794903c730a4/Emby.Server.Implementations/Library/Resolvers/TV/SeasonResolver.cs#L70
                if (seasonNumber is null)
                {
                    seasonNumber = this.GuessSeasonNumberByDirectoryName(info.Path);
                }

                // 搜索豆瓣季 id
                if (string.IsNullOrEmpty(seasonSid))
                {
                    seasonSid = await this.GuessDoubanSeasonId(sid, seriesTmdbId, seasonNumber, info, cancellationToken).ConfigureAwait(false);
                }

                // 获取季豆瓣数据
                if (!string.IsNullOrEmpty(seasonSid))
                {
                    var subject = await this.DoubanApi.GetMovieAsync(seasonSid, cancellationToken).ConfigureAwait(false);
                    if (subject != null)
                    {
                        var movie = new Season
                        {
                            ProviderIds = new Dictionary<string, string> { { DoubanProviderId, subject.Sid } },
                            Name = subject.Name,
                            CommunityRating = subject.Rating,
                            Overview = subject.Intro,
                            ProductionYear = subject.Year,
                            Genres = subject.Genres.ToArray(),
                            PremiereDate = subject.ScreenTime,  // 发行日期
                            IndexNumber = seasonNumber,
                        };

                        result.Item = movie;
                        result.HasMetadata = true;
                        this.Log("已找到季 Douban sid. name: {0} sid: {1}", info.Name, seasonSid);
                        return result;
                    }
                }
                else
                {
                    this.Log("未找到季 Douban id. name: {0}", info.Name);
                }

                // 豆瓣找不到季数据，尝试获取tmdb的季数据
                if (string.IsNullOrEmpty(seasonSid) && !string.IsNullOrWhiteSpace(seriesTmdbId) && seasonNumber.HasValue && seasonNumber >= 0)
                {
                    var tmdbResult = await this.GetMetadataByTmdb(info, seriesTmdbId, seasonNumber.Value, cancellationToken).ConfigureAwait(false);
                    if (tmdbResult != null)
                    {
                        return tmdbResult;
                    }
                }

                // 从豆瓣获取不到季信息
                return result;
            }

            // series使用TMDB元数据来源
            // tmdb季级没有对应id，只通过indexNumber区分
            if (metaSource == MetaSource.Tmdb && !string.IsNullOrEmpty(seriesTmdbId))
            {
                return await this.GetMetadataByTmdb(info, seriesTmdbId, seasonNumber, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }

        public async Task<string?> GuessDoubanSeasonId(string? sid, string? seriesTmdbId, int? seasonNumber, ItemLookupInfo info, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(info);
            if (string.IsNullOrEmpty(sid))
            {
                return null;
            }

            // 没有季文件夹或季文件夹名不规范时（即虚拟季），info.Path 会为空，seasonNumber 为 null
            if (string.IsNullOrEmpty(info.Path) && !seasonNumber.HasValue)
            {
                return null;
            }

            // 从季文件夹名属性格式获取，如 [douban-12345] 或 [doubanid-12345]
            var fileName = GetOriginalFileName(info);
            var doubanId = this.RegDoubanIdAttribute.FirstMatchGroup(fileName);
            if (!string.IsNullOrWhiteSpace(doubanId))
            {
                this.Log("已通过属性找到季 Douban id. doubanId: {0}", doubanId);
                return doubanId;
            }

            // 从sereis获取正确名称，info.Name当是标准格式如S01等时，会变成第x季，非标准名称默认文件名
            var series = await this.DoubanApi.GetMovieAsync(sid, cancellationToken).ConfigureAwait(false);
            if (series == null)
            {
                return null;
            }

            var seriesName = this.RemoveSeasonSuffix(series.Name);

            // 没有季id，但存在tmdbid，尝试从tmdb获取对应季的年份信息，用于从豆瓣搜索对应季数据
            var seasonYear = 0;
            if (!string.IsNullOrEmpty(seriesTmdbId) && (seasonNumber.HasValue && seasonNumber > 0))
            {
                var season = await this.TmdbApi
                    .GetSeasonAsync(seriesTmdbId.ToInt(), seasonNumber.Value, info.MetadataLanguage, info.MetadataLanguage, cancellationToken)
                    .ConfigureAwait(false);
                seasonYear = season?.AirDate?.Year ?? 0;
            }

            if (!string.IsNullOrEmpty(seriesName) && seasonYear > 0)
            {
                var seasonSid = await this.GuestDoubanSeasonByYearAsync(seriesName, seasonYear, seasonNumber, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(seasonSid))
                {
                    return seasonSid;
                }
            }

            // 通过季名匹配douban id，作为关闭tmdb api/api超时的后备方法使用
            if (!string.IsNullOrEmpty(seriesName) && seasonNumber.HasValue && seasonNumber > 0)
            {
                return await this.GuestDoubanSeasonBySeasonNameAsync(seriesName, seasonNumber, cancellationToken).ConfigureAwait(false);
            }

            return null;
        }

        public async Task<MetadataResult<Season>> GetMetadataByTmdb(SeasonInfo info, string? seriesTmdbId, int? seasonNumber, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(info);
            var result = new MetadataResult<Season>();

            if (string.IsNullOrEmpty(seriesTmdbId))
            {
                return result;
            }

            if (seasonNumber is null or 0)
            {
                return result;
            }

            if (TmdbEpisodeGroupMapping.TryGetGroupId(Config.TmdbEpisodeGroupMap, seriesTmdbId, out var groupId))
            {
                this.Log("TMDb 剧集组命中（季）: seriesId={0} groupId={1} season={2}", seriesTmdbId, groupId, seasonNumber);
                var group = await this.TmdbApi
                    .GetEpisodeGroupByIdAsync(groupId, info.MetadataLanguage, cancellationToken)
                    .ConfigureAwait(false);
                var seasonGroup = group?.Groups.FirstOrDefault(g => g.Order == seasonNumber);
                if (seasonGroup != null)
                {
                    this.Log("TMDb 剧集组已解析（季）: seriesId={0} groupId={1} season={2} name={3}", seriesTmdbId, groupId, seasonNumber, seasonGroup.Name);
                    var seasonGroupName = seasonGroup.Name?.Trim();
                    var seasonName = seasonGroupName;
                    if (ShouldPreserveExistingSeasonTitle(info.Name, seasonGroupName))
                    {
                        seasonName = info.Name.Trim();
                    }

                    result.Item = new Season
                    {
                        Name = seasonName,
                        IndexNumber = seasonNumber,
                    };
                    result.HasMetadata = true;
                    return result;
                }
            }

            var seasonResult = await this.TmdbApi
                .GetSeasonAsync(seriesTmdbId.ToInt(), seasonNumber ?? 0, info.MetadataLanguage, info.MetadataLanguage, cancellationToken)
                .ConfigureAwait(false);
            if (seasonResult == null)
            {
                this.Log("未找到 TMDb 季数据. name: {0} seriesTmdbId: {1} seasonNumber: {2}", info.Name, seriesTmdbId, seasonNumber);
                return result;
            }

            result.HasMetadata = true;
            result.Item = new Season
            {
                Name = seasonResult.Name,
                IndexNumber = seasonNumber,
                Overview = seasonResult.Overview,
                PremiereDate = seasonResult.AirDate,
                ProductionYear = seasonResult.AirDate?.Year,
            };

            if (!string.IsNullOrEmpty(seasonResult.ExternalIds?.TvdbId))
            {
                result.Item.SetProviderId(MetadataProvider.Tvdb, seasonResult.ExternalIds.TvdbId);
            }

            return result;
        }

        private static bool ShouldPreserveExistingSeasonTitle(string? currentSeasonTitle, string? seasonGroupName)
        {
            if (string.IsNullOrWhiteSpace(currentSeasonTitle) || string.IsNullOrWhiteSpace(seasonGroupName))
            {
                return false;
            }

            var trimmedSeasonGroupName = seasonGroupName.Trim();
            var hasAsciiLetter = false;

            foreach (var c in trimmedSeasonGroupName)
            {
                if (IsCjkIdeographOrKanaOrHangul(c))
                {
                    return false;
                }

                if (char.IsWhiteSpace(c))
                {
                    continue;
                }

                if (IsAsciiLetter(c))
                {
                    hasAsciiLetter = true;
                    continue;
                }

                if (char.IsDigit(c) || IsAllowedEnglishStylePunctuation(c))
                {
                    continue;
                }

                return false;
            }

            return hasAsciiLetter;
        }

        private static bool IsAsciiLetter(char c)
        {
            return (c is >= 'A' and <= 'Z') || (c is >= 'a' and <= 'z');
        }

        private static bool IsAllowedEnglishStylePunctuation(char c)
        {
            return c is '-' or '_' or '.' or ':' or '/' or '&' or '\'' or '(' or ')' or '+' or '!' or '#';
        }

        private static bool IsCjkIdeographOrKanaOrHangul(char c)
        {
            return c is >= '\u3400' and <= '\u4DBF'
                or >= '\u4E00' and <= '\u9FFF'
                or >= '\u3040' and <= '\u309F'
                or >= '\u30A0' and <= '\u30FF'
                or >= '\u31F0' and <= '\u31FF'
                or >= '\u1100' and <= '\u11FF'
                or >= '\u3130' and <= '\u318F'
                or >= '\uAC00' and <= '\uD7AF'
                or >= '\uFF66' and <= '\uFF9D';
        }
    }
}
