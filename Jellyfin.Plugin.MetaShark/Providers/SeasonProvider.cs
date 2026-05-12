// <copyright file="SeasonProvider.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Jellyfin.Data.Enums;
    using Jellyfin.Plugin.MetaShark.Api;
    using Jellyfin.Plugin.MetaShark.Core;
    using Jellyfin.Plugin.MetaShark.Model;
    using Jellyfin.Plugin.MetaShark.Providers.Llm;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.TV;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Providers;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;

    [SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1202:Elements should be ordered by access", Justification = "Keep LLM helper methods near the flow they support.")]
    [SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1204:Static elements should appear before non-static members", Justification = "Keep provider orchestration helpers near the flow they support.")]
    public class SeasonProvider : BaseProvider, IRemoteMetadataProvider<Season, SeasonInfo>
    {
        private readonly ILlmMetadataAssistService? llmMetadataAssistService;
        private readonly ILlmExternalIdResolutionService? llmExternalIdResolutionService;
        private readonly LlmAssistTriggerPolicy llmAssistTriggerPolicy = new LlmAssistTriggerPolicy();
        private readonly LlmMetadataMergePolicy llmMetadataMergePolicy = new LlmMetadataMergePolicy();

        public SeasonProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, DoubanApi doubanApi, TmdbApi tmdbApi, OmdbApi omdbApi, ImdbApi imdbApi, ILlmMetadataAssistService? llmMetadataAssistService = null, ILlmExternalIdResolutionService? llmExternalIdResolutionService = null)
            : base(httpClientFactory, loggerFactory.CreateLogger<SeasonProvider>(), libraryManager, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi)
        {
            this.llmMetadataAssistService = llmMetadataAssistService;
            this.llmExternalIdResolutionService = llmExternalIdResolutionService;
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
            if (metaSource == MetaSource.Tmdb && string.IsNullOrWhiteSpace(seriesTmdbId))
            {
                metaSource = MetaSource.None;
            }

            info.SeriesProviderIds.TryGetValue(DoubanProviderId, out var sid);
            var seasonNumber = info.IndexNumber; // S00/Season 00特典目录会为0
            var seasonSid = info.GetProviderId(DoubanProviderId);
            var fileName = Path.GetFileName(info.Path);
            var parentSeriesResolution = string.IsNullOrWhiteSpace(seriesTmdbId)
                ? await this.TryResolveParentSeriesTmdbIdWithLlmAsync(info, seasonNumber, semantic, cancellationToken).ConfigureAwait(false)
                : ParentSeriesLlmResolutionResult.NotTriggered();
            if (parentSeriesResolution.RequiresManualSeriesCorrection)
            {
                return result;
            }

            var seriesTmdbIdForSeasonQuery = string.IsNullOrWhiteSpace(seriesTmdbId) ? parentSeriesResolution.VerifiedTmdbId : seriesTmdbId;
            this.Log("开始获取季元数据. name: {0} fileName: {1} seasonNumber: {2} seriesTmdbId: {3} sid: {4} metaSource: {5} enableTmdb: {6}", info.Name, fileName, info.IndexNumber, seriesTmdbId, sid, metaSource, Config.EnableTmdb);
            if (!doubanAllowed)
            {
                if (!string.IsNullOrWhiteSpace(seriesTmdbIdForSeasonQuery))
                {
                    var tmdbOnlyResult = await this.GetMetadataByTmdb(info, seriesTmdbIdForSeasonQuery, seasonNumber, cancellationToken).ConfigureAwait(false);
                    if (tmdbOnlyResult.HasMetadata)
                    {
                        return tmdbOnlyResult;
                    }
                }

                return await this.TryGetLlmAssistedMetadataAsync(info, seasonNumber, semantic, result, cancellationToken).ConfigureAwait(false);
            }

            // seasonNumber 为 null 有三种情况：
            // 1. 没有季文件夹时，即虚拟季，info.Path 为空
            // 2. 一般不规范文件夹命名，没法被 EpisodeResolver 解析的，info.Path 不为空，如：摇曳露营△
            // 3. 特殊不规范文件夹命名，能被 EpisodeResolver 错误解析，这时被当成了视频文件，相当于没有季文件夹，info.Path 为空，如：冰与火之歌 S02.列王的纷争.2012.1080p.Blu-ray.x265.10bit.AC3
            //    相关代码：https://github.com/jellyfin/jellyfin/blob/dc2eca9f2ca259b46c7b53f59251794903c730a4/Emby.Server.Implementations/Library/Resolvers/TV/SeasonResolver.cs#L70
            if (seasonNumber is null)
            {
                seasonNumber = this.GuessSeasonNumberByDirectoryName(info.Path);
            }

            if (!string.IsNullOrEmpty(sid))
            {
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

                    this.Log("季 Douban 数据为空. name: {0} sid: {1}", info.Name, seasonSid);
                }
                else
                {
                    this.Log("未找到季 Douban id. name: {0}", info.Name);
                }
            }
            else
            {
                this.Log("剧集 Douban id 为空，跳过季 Douban 元数据. name: {0}", info.Name);
            }

            // 豆瓣找不到季数据，尝试获取tmdb的季数据
            if (!string.IsNullOrWhiteSpace(seriesTmdbIdForSeasonQuery) && seasonNumber.HasValue && seasonNumber >= 0)
            {
                var tmdbResult = await this.GetMetadataByTmdb(info, seriesTmdbIdForSeasonQuery, seasonNumber.Value, cancellationToken).ConfigureAwait(false);
                if (tmdbResult != null && tmdbResult.HasMetadata)
                {
                    return tmdbResult;
                }
            }

            // 从豆瓣获取不到季信息
            return await this.TryGetLlmAssistedMetadataAsync(info, seasonNumber, semantic, result, cancellationToken).ConfigureAwait(false);
        }

        private static bool HasCompleteLlmConfiguration(Configuration.PluginConfiguration configuration)
        {
            return configuration.EnableLlmAssist
                && !string.IsNullOrWhiteSpace(configuration.LlmBaseUrl)
                && !string.IsNullOrWhiteSpace(configuration.LlmModel)
                && !string.IsNullOrWhiteSpace(configuration.LlmApiKey);
        }

        private static SeasonInfo CreateSafeLlmExternalIdSeasonLookupInfo(SeasonInfo info, int? seasonNumber)
        {
            var safePath = Config.LlmAllowRelativePathContext
                ? LlmRelativePathSanitizer.Sanitize(info.Path, Array.Empty<string?>(), nameof(Season))
                : string.Empty;
            var parentProviderIds = CreatePublicProviderIdCopy(info.SeriesProviderIds);

            return new SeasonInfo
            {
                Name = info.Name,
                Path = safePath,
                MetadataLanguage = info.MetadataLanguage,
                MetadataCountryCode = info.MetadataCountryCode,
                Year = info.Year,
                ParentIndexNumber = seasonNumber,
                ProviderIds = parentProviderIds,
                SeriesProviderIds = parentProviderIds == null ? null : new Dictionary<string, string>(parentProviderIds, StringComparer.OrdinalIgnoreCase),
            };
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
                if (TryNormalizePublicProviderIdKey(providerId.Key, out var key) && !string.IsNullOrWhiteSpace(providerId.Value))
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

        private static string? GetParentSeriesTmdbIdFromVerifiedCandidates(LlmExternalIdResolutionResult resolutionResult)
        {
            return resolutionResult.VerifiedCandidates
                .FirstOrDefault(candidate => string.Equals(candidate.Provider, "TMDb", StringComparison.Ordinal)
                    && string.Equals(candidate.MediaType, nameof(Series), StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(candidate.Id))
                ?.Id;
        }

        private static bool HasNonTmdbParentSeriesEvidence(SeasonInfo info)
        {
            if (info.SeriesProviderIds == null || info.SeriesProviderIds.Count == 0)
            {
                return false;
            }

            return info.SeriesProviderIds.Any(entry => !string.IsNullOrWhiteSpace(entry.Value)
                && !string.Equals(entry.Key, MetadataProvider.Tmdb.ToString(), StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entry.Key, MetaSharkPlugin.ProviderId, StringComparison.OrdinalIgnoreCase));
        }

        private async Task<ParentSeriesLlmResolutionResult> TryResolveParentSeriesTmdbIdWithLlmAsync(SeasonInfo info, int? seasonNumber, DefaultScraperSemantic semantic, CancellationToken cancellationToken)
        {
            if (this.llmExternalIdResolutionService == null || !HasCompleteLlmConfiguration(Config))
            {
                LlmObservabilityLog.LogLlmAssistRejected(this.Logger, "LlmConfigurationMissing", nameof(Season), semantic, false);
                return ParentSeriesLlmResolutionResult.NotTriggered();
            }

            var triggerDecision = this.llmAssistTriggerPolicy.Evaluate(new LlmAssistTriggerContext
            {
                Configuration = Config,
                Semantic = semantic,
                MediaType = nameof(Season),
                IsImageProvider = false,
                HttpContext = this.HttpContextAccessor.HttpContext,
            });
            if (!triggerDecision.ShouldTrigger)
            {
                LlmObservabilityLog.LogLlmAssistRejected(this.Logger, triggerDecision.Reason, nameof(Season), semantic, false);
                return ParentSeriesLlmResolutionResult.NotTriggered();
            }

            var request = new LlmExternalIdResolutionRequest
            {
                Configuration = Config,
                LookupInfo = CreateSafeLlmExternalIdSeasonLookupInfo(info, seasonNumber),
                MediaType = nameof(Season),
                Name = info.Name,
                Year = info.Year,
                Semantic = semantic,
                IsImageProvider = false,
                HttpContext = this.HttpContextAccessor.HttpContext,
                LibraryRoots = Array.Empty<string?>(),
            };
            var existingProviderDecision = await this.llmExternalIdResolutionService.EvaluateExistingProviderIdsAsync(request, cancellationToken).ConfigureAwait(false);
            if (!existingProviderDecision.ShouldTrigger)
            {
                LlmObservabilityLog.LogLlmAssistRejected(this.Logger, existingProviderDecision.Reason, nameof(Season), semantic, false);
                return ParentSeriesLlmResolutionResult.NotTriggered();
            }

            var resolutionResult = await this.llmExternalIdResolutionService.ResolveAsync(request, cancellationToken).ConfigureAwait(false);
            var verifiedParentSeriesTmdbId = GetParentSeriesTmdbIdFromVerifiedCandidates(resolutionResult);
            if (string.IsNullOrWhiteSpace(verifiedParentSeriesTmdbId))
            {
                return ParentSeriesLlmResolutionResult.NotTriggered();
            }

            if (string.Equals(existingProviderDecision.Reason, "StaleExternalIdConflict", StringComparison.Ordinal)
                && HasNonTmdbParentSeriesEvidence(info))
            {
                LlmObservabilityLog.LogLlmAssistRejected(this.Logger, "ParentSeriesCorrectionCandidate", nameof(Season), semantic, false);
                return ParentSeriesLlmResolutionResult.NeedsManual(null);
            }

            return ParentSeriesLlmResolutionResult.Verified(verifiedParentSeriesTmdbId);
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

            return result;
        }

        private async Task<MetadataResult<Season>> TryGetLlmAssistedMetadataAsync(
            SeasonInfo info,
            int? seasonNumber,
            DefaultScraperSemantic semantic,
            MetadataResult<Season> fallbackResult,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(info);
            ArgumentNullException.ThrowIfNull(fallbackResult);
            if (!Config.LlmAllowTextCompletion)
            {
                LlmObservabilityLog.LogLlmAssistRejected(this.Logger, "TextCompletionDisabled", nameof(Season), semantic, false);
                return fallbackResult;
            }

            if (this.llmMetadataAssistService == null)
            {
                LlmObservabilityLog.LogLlmAssistRejected(this.Logger, "LlmConfigurationMissing", nameof(Season), semantic, false);
                return fallbackResult;
            }

            var triggerDecision = this.llmAssistTriggerPolicy.Evaluate(new LlmAssistTriggerContext
            {
                Configuration = Config,
                Semantic = semantic,
                MediaType = nameof(Season),
                IsImageProvider = false,
                HttpContext = this.HttpContextAccessor.HttpContext,
            });
            if (!triggerDecision.ShouldTrigger)
            {
                LlmObservabilityLog.LogLlmAssistRejected(this.Logger, triggerDecision.Reason, nameof(Season), semantic, false);
                return fallbackResult;
            }

            var request = new LlmScrapingAssistRequest
            {
                Configuration = Config,
                LookupInfo = CreateLlmLookupInfo(info),
                MediaType = nameof(Season),
                Semantic = semantic,
                IsImageProvider = false,
                HttpContext = this.HttpContextAccessor.HttpContext,
                LibraryRoots = Array.Empty<string?>(),
            };
            var assistResult = await this.llmMetadataAssistService.AssistAsync(request, cancellationToken).ConfigureAwait(false);

            if (assistResult.Status != LlmScrapingAssistStatus.Succeeded || assistResult.Suggestion == null)
            {
                this.Log("季 LLM 辅助未生成可用元数据. name: {0} status: {1} diagnostic: {2}", info.Name, assistResult.Status, assistResult.Diagnostic);
                return fallbackResult;
            }

            var llmResult = new MetadataResult<Season>
            {
                Item = new Season
                {
                    IndexNumber = seasonNumber,
                },
            };
            var mergeResult = this.llmMetadataMergePolicy.Apply(llmResult, assistResult.Suggestion, Config);
            if (!mergeResult.Applied)
            {
                this.Log("季 LLM 辅助跳过合并. name: {0} reason: {1}", info.Name, mergeResult.Reason);
                return fallbackResult;
            }

            llmResult.HasMetadata = true;
            this.Log("季 LLM 辅助已填充文本元数据. name: {0} fields: {1}", info.Name, string.Join(",", mergeResult.ChangedFields));
            return llmResult;
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

        private static SeasonInfo CreateLlmLookupInfo(SeasonInfo info)
        {
            return new SeasonInfo
            {
                Name = info.Name,
                Path = LlmRelativePathSanitizer.Sanitize(info.Path, Array.Empty<string?>(), nameof(Season)),
                MetadataLanguage = info.MetadataLanguage,
                Year = info.Year,
                IndexNumber = info.IndexNumber,
                ParentIndexNumber = info.ParentIndexNumber,
                ProviderIds = CreateProviderIdPresenceMap(info.ProviderIds),
                SeriesProviderIds = CreateProviderIdPresenceMap(info.SeriesProviderIds),
            };
        }

        private static Dictionary<string, string>? CreateProviderIdPresenceMap(Dictionary<string, string>? providerIds)
        {
            if (providerIds == null || providerIds.Count == 0)
            {
                return null;
            }

            return providerIds.Keys.ToDictionary(key => key, _ => "present", StringComparer.OrdinalIgnoreCase);
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

        private sealed class ParentSeriesLlmResolutionResult
        {
            private ParentSeriesLlmResolutionResult(string? verifiedTmdbId, bool requiresManualSeriesCorrection)
            {
                this.VerifiedTmdbId = verifiedTmdbId;
                this.RequiresManualSeriesCorrection = requiresManualSeriesCorrection;
            }

            public string? VerifiedTmdbId { get; }

            public bool RequiresManualSeriesCorrection { get; }

            public static ParentSeriesLlmResolutionResult NotTriggered()
            {
                return new ParentSeriesLlmResolutionResult(null, false);
            }

            public static ParentSeriesLlmResolutionResult Verified(string tmdbId)
            {
                return new ParentSeriesLlmResolutionResult(tmdbId, false);
            }

            public static ParentSeriesLlmResolutionResult NeedsManual(string? tmdbId)
            {
                return new ParentSeriesLlmResolutionResult(tmdbId, true);
            }
        }
    }
}
