// <copyright file="MovieProvider.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using AngleSharp.Text;
    using Jellyfin.Data.Enums;
    using Jellyfin.Plugin.MetaShark.Api;
    using Jellyfin.Plugin.MetaShark.Core;
    using Jellyfin.Plugin.MetaShark.Model;
    using Jellyfin.Plugin.MetaShark.Providers.Llm;
    using Jellyfin.Plugin.MetaShark.Workers;
    using MediaBrowser.Controller.Entities;
    using MediaBrowser.Controller.Entities.Movies;
    using MediaBrowser.Controller.Library;
    using MediaBrowser.Controller.Providers;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Providers;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;
    using TMDbLib.Objects.Search;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1204:Static elements should appear before non-static members", Justification = "Keep provider flow before helper methods.")]
    public class MovieProvider : BaseProvider, IRemoteMetadataProvider<Movie, MovieInfo>
    {
        private readonly ILlmMetadataAssistService? llmMetadataAssistService;
        private readonly ILlmExternalIdResolutionService? llmExternalIdResolutionService;
        private readonly LlmAssistTriggerPolicy llmAssistTriggerPolicy = new LlmAssistTriggerPolicy();
        private readonly LlmTmdbIdCorrectionTriggerPolicy llmTmdbIdCorrectionTriggerPolicy = new LlmTmdbIdCorrectionTriggerPolicy();
        private readonly LlmMetadataMergePolicy llmMetadataMergePolicy = new LlmMetadataMergePolicy();
        private readonly LlmScrapeContextBuilder llmScrapeContextBuilder = new LlmScrapeContextBuilder();
        private readonly LlmScrapeMismatchDetector llmScrapeMismatchDetector = new LlmScrapeMismatchDetector();
        private readonly IMovieSeriesPeopleOverwriteRefreshCandidateStore? movieSeriesPeopleOverwriteRefreshCandidateStore;

        public MovieProvider(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, ILibraryManager libraryManager, IHttpContextAccessor httpContextAccessor, DoubanApi doubanApi, TmdbApi tmdbApi, OmdbApi omdbApi, ImdbApi imdbApi, IMovieSeriesPeopleOverwriteRefreshCandidateStore? movieSeriesPeopleOverwriteRefreshCandidateStore = null, ILlmMetadataAssistService? llmMetadataAssistService = null, ILlmExternalIdResolutionService? llmExternalIdResolutionService = null)
            : base(httpClientFactory, loggerFactory.CreateLogger<MovieProvider>(), libraryManager, httpContextAccessor, doubanApi, tmdbApi, omdbApi, imdbApi)
        {
            this.movieSeriesPeopleOverwriteRefreshCandidateStore = movieSeriesPeopleOverwriteRefreshCandidateStore ?? InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore.Shared;
            this.llmMetadataAssistService = llmMetadataAssistService;
            this.llmExternalIdResolutionService = llmExternalIdResolutionService;
        }

        public string Name => MetaSharkPlugin.PluginName;

        /// <inheritdoc />
        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(searchInfo);
            this.Log("开始搜索电影候选. name: {0}", searchInfo.Name);
            var result = new List<RemoteSearchResult>();
            if (string.IsNullOrEmpty(searchInfo.Name))
            {
                return result;
            }

            // 从douban搜索
            var res = await this.DoubanApi.SearchMovieAsync(searchInfo.Name, cancellationToken).ConfigureAwait(false);
            result.AddRange(res.Take(Configuration.PluginConfiguration.MAXSEARCHRESULT).Select(x =>
            {
                return new RemoteSearchResult
                {
                    // 注意：jellyfin 会判断电影所有 provider id 是否有相同的，有相同的值就会认为是同一影片，会被合并不返回，必须保持 provider id 的唯一性
                    // 这里 MetaSharkPlugin.ProviderId 的值做这么复杂，是为了保持唯一
                    ProviderIds = new Dictionary<string, string> { { DoubanProviderId, x.Sid }, { MetaSharkPlugin.ProviderId, $"{MetaSource.Douban}_{x.Sid}" } },
                    ImageUrl = this.GetProxyImageUrl(new Uri(x.Img, UriKind.Absolute)).ToString(),
                    ProductionYear = x.Year,
                    Name = x.Name,
                };
            }));

            // 从tmdb搜索
            if (Config.EnableTmdbSearch)
            {
                var tmdbList = await this.TmdbApi.SearchMovieAsync(searchInfo.Name, searchInfo.MetadataLanguage, cancellationToken).ConfigureAwait(false);
                result.AddRange(tmdbList.Take(Configuration.PluginConfiguration.MAXSEARCHRESULT).Select(x =>
                {
                    return new RemoteSearchResult
                    {
                        // 注意：jellyfin 会判断电影所有 provider id 是否有相同的，有相同的值就会认为是同一影片，会被合并不返回，必须保持 provider id 的唯一性
                        // 这里 MetaSharkPlugin.ProviderId 的值做这么复杂，是为了保持唯一
                        ProviderIds = new Dictionary<string, string> { { MetadataProvider.Tmdb.ToString(), x.Id.ToString(CultureInfo.InvariantCulture) }, { MetaSharkPlugin.ProviderId, $"{MetaSource.Tmdb}_{x.Id}" } },
                        Name = string.Format(CultureInfo.InvariantCulture, "[TMDB]{0}", x.Title ?? x.OriginalTitle),
                        ImageUrl = this.TmdbApi.GetPosterUrl(x.PosterPath)?.ToString(),
                        Overview = x.Overview,
                        ProductionYear = x.ReleaseDate?.Year,
                    };
                }));
            }

            return result;
        }

        /// <inheritdoc />
        public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(info);
            var fileName = GetOriginalFileName(info);
            var result = new MetadataResult<Movie>();
            var semantic = this.ResolveMetadataSemantic(info);
            var doubanAllowed = IsDoubanAllowed(semantic);

            // 使用刷新元数据时，providerIds会保留旧有值，只有识别/新增才会没值
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
            var llmAssistResult = LlmScrapingAssistResult.NotTriggered("AuthoritativeMetadataPresent");
            var externalIdResolutionResult = LlmExternalIdResolutionResult.NotTriggered("AuthoritativeMetadataPresent");
            var tmdbCorrectionResult = await this.TryResolveMovieTmdbCorrectionAsync(info, semantic, originalTmdbId, cancellationToken).ConfigureAwait(false);
            if (tmdbCorrectionResult.ShouldReplace && !string.IsNullOrWhiteSpace(tmdbCorrectionResult.ReplacementTmdbId))
            {
                tmdbId = tmdbCorrectionResult.ReplacementTmdbId;
                hasVerifiedTmdbCorrection = true;
            }

            this.Log("开始获取电影元数据. name: {0} fileName: {1} metaSource: {2} enableTmdb: {3}", info.Name, fileName, metaSource, Config.EnableTmdb);
            if (!hasDoubanMeta && !hasTmdbMeta)
            {
                // 处理extras影片
                var extraResult = this.HandleExtraType(info);
                if (extraResult != null)
                {
                    return FinalizeMetadataResult(extraResult, originalTmdbId, originalPublicProviderIds, hasVerifiedTmdbCorrection);
                }

                llmAssistResult = await this.TryAssistMovieMetadataWithLlmAsync(info, semantic, cancellationToken).ConfigureAwait(false);
                var llmSearchHints = llmAssistResult.SearchHints;
                var preferLlmSearchHints = ShouldPreferLlmMovieSearchHints(info, fileName, llmSearchHints);

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

            if (string.IsNullOrWhiteSpace(tmdbId))
            {
                externalIdResolutionResult = await this.TryResolveMissingMovieProviderIdsWithLlmAsync(info, semantic, cancellationToken).ConfigureAwait(false);
                tmdbId = GetProviderIdWriteValue(externalIdResolutionResult, MetadataProvider.Tmdb.ToString()) ?? tmdbId;
                sid = GetProviderIdWriteValue(externalIdResolutionResult, DoubanProviderId) ?? sid;
                effectiveSid = doubanAllowed ? sid : null;
            }

            if (!tmdbSourceIsPrimary && !string.IsNullOrEmpty(effectiveSid))
            {
                this.Log("通过 Douban 获取电影元数据. sid: \"{0}\"", effectiveSid);
                var subject = await this.DoubanApi.GetMovieAsync(effectiveSid, cancellationToken).ConfigureAwait(false);
                if (subject == null)
                {
                    if (string.IsNullOrEmpty(tmdbId) && Config.EnableTmdbMatch)
                    {
                        tmdbId = await this.GuestByTmdbAsync(info, cancellationToken).ConfigureAwait(false);
                        if (string.IsNullOrEmpty(tmdbId))
                        {
                            tmdbId = await this.GuessByTmdbWithLlmHintsAsync(llmAssistResult.SearchHints, info, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    if (!string.IsNullOrEmpty(tmdbId))
                    {
                        var tmdbFallbackResult = await this.GetMetadataByTmdb(tmdbId, info, cancellationToken).ConfigureAwait(false);
                        ApplyLlmExternalProviderIdWrites(tmdbFallbackResult, externalIdResolutionResult);
                        this.ApplyLlmTextCompletion(tmdbFallbackResult, llmAssistResult);
                        return FinalizeMetadataResult(tmdbFallbackResult, originalTmdbId, originalPublicProviderIds, hasVerifiedTmdbCorrection);
                    }

                    return FinalizeMetadataResult(result, originalTmdbId, originalPublicProviderIds, hasVerifiedTmdbCorrection);
                }

                var correctionResult = await this.TryCorrectDoubanMismatchWithLlmAsync(subject, info, semantic, llmAssistResult, cancellationToken).ConfigureAwait(false);
                if (correctionResult.Subject != null)
                {
                    subject = correctionResult.Subject;
                    effectiveSid = correctionResult.Subject.Sid;
                    sid = correctionResult.Subject.Sid;
                    llmAssistResult = correctionResult.AssistResult;
                }

                var movie = new Movie
                {
                    // 这里 MetaSharkPlugin.ProviderId 的值做这么复杂，是为了保持唯一
                    ProviderIds = new Dictionary<string, string> { { DoubanProviderId, subject.Sid }, { MetaSharkPlugin.ProviderId, $"{MetaSource.Douban}_{subject.Sid}" } },
                    Name = subject.Name,
                    OriginalTitle = subject.OriginalName,
                    CommunityRating = subject.Rating,
                    Overview = subject.Intro,
                    ProductionYear = subject.Year,
                    HomePageUrl = "https://www.douban.com",
                    Genres = subject.Genres.ToArray(),
                    PremiereDate = subject.ScreenTime,
                };
                if (!string.IsNullOrEmpty(tmdbId))
                {
                    movie.SetProviderId(MetadataProvider.Tmdb, tmdbId);
                }

                if (!string.IsNullOrEmpty(subject.Imdb))
                {
                    var newImdbId = await this.CheckNewImdbID(subject.Imdb, cancellationToken).ConfigureAwait(false);
                    subject.Imdb = newImdbId;
                    movie.SetProviderId(MetadataProvider.Imdb, newImdbId);

                    // 通过imdb获取TMDB id
                    if (string.IsNullOrEmpty(tmdbId))
                    {
                        var newTmdbId = await this.GetTmdbIdByImdbAsync(subject.Imdb, info.MetadataLanguage, info, cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(newTmdbId))
                        {
                            tmdbId = newTmdbId;
                            movie.SetProviderId(MetadataProvider.Tmdb, tmdbId);
                        }
                    }
                }

                // 尝试通过搜索匹配获取tmdbId
                if (string.IsNullOrEmpty(tmdbId) && subject.Year > 0)
                {
                    var newTmdbId = await this.GuestByTmdbAsync(subject.Name, subject.Year, info, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(newTmdbId))
                    {
                        tmdbId = newTmdbId;
                        movie.SetProviderId(MetadataProvider.Tmdb, tmdbId);
                    }
                }

                // 通过imdb获取电影系列信息
                if (!string.IsNullOrEmpty(tmdbId))
                {
                    var belongCollection = await this.GetTmdbCollection(info, tmdbId, cancellationToken).ConfigureAwait(false);
                    if (belongCollection != null && !string.IsNullOrEmpty(belongCollection.Name))
                    {
                        movie.CollectionName = belongCollection.Name;
                    }
                }

                // 通过imdb获取电影分级信息
                if (Config.EnableTmdbOfficialRating && !string.IsNullOrEmpty(tmdbId))
                {
                    var officialRating = await this.GetTmdbOfficialRating(info, tmdbId, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(officialRating))
                    {
                        movie.OfficialRating = officialRating;
                    }
                }

                ApplyLlmExternalProviderIdWrites(movie.ProviderIds, externalIdResolutionResult);

                result.Item = movie;
                result.QueriedById = true;
                result.HasMetadata = true;

                if (!string.IsNullOrEmpty(tmdbId))
                {
                    var acceptedPeopleCount = await this.TryAddTmdbPeopleAsync(tmdbId, info, result, cancellationToken).ConfigureAwait(false);
                    this.TryQueueSearchMissingMetadataOverwriteCandidate(info, tmdbId, result.People, acceptedPeopleCount);
                }

                this.ApplyLlmTextCompletion(result, llmAssistResult);

                return FinalizeMetadataResult(result, originalTmdbId, originalPublicProviderIds, hasVerifiedTmdbCorrection);
            }

            if (!string.IsNullOrEmpty(tmdbId) && (!doubanAllowed || tmdbSourceIsPrimary || string.IsNullOrEmpty(effectiveSid)))
            {
                var tmdbResult = await this.GetMetadataByTmdb(tmdbId, info, cancellationToken).ConfigureAwait(false);
                ApplyLlmExternalProviderIdWrites(tmdbResult, externalIdResolutionResult);
                this.ApplyLlmTextCompletion(tmdbResult, llmAssistResult);
                return FinalizeMetadataResult(tmdbResult, originalTmdbId, originalPublicProviderIds, hasVerifiedTmdbCorrection);
            }

            this.Log("电影匹配失败，可检查年份是否与豆瓣一致，或是否需要登录访问. name: {0} year: {1}", info.Name, info.Year);
            return FinalizeMetadataResult(result, originalTmdbId, originalPublicProviderIds, hasVerifiedTmdbCorrection);
        }

        private static MetadataResult<Movie> FinalizeMetadataResult(MetadataResult<Movie> result, string? originalTmdbId, IReadOnlyDictionary<string, string>? originalPublicProviderIds, bool hasVerifiedCorrection)
        {
            TmdbProviderIdPreservationHelper.PreserveMovieTmdbId(originalTmdbId, result.Item, hasVerifiedCorrection);
            PreserveNonTmdbProviderIdsAfterCorrection(result.Item, originalPublicProviderIds, hasVerifiedCorrection);
            return result;
        }

        private static void PreserveNonTmdbProviderIdsAfterCorrection(Movie? movie, IReadOnlyDictionary<string, string>? originalPublicProviderIds, bool hasVerifiedCorrection)
        {
            if (!hasVerifiedCorrection || movie == null || originalPublicProviderIds == null)
            {
                return;
            }

            PreserveProviderId(movie, originalPublicProviderIds, MetadataProvider.Imdb.ToString());
            PreserveProviderId(movie, originalPublicProviderIds, MetadataProvider.Tvdb.ToString());
            PreserveProviderId(movie, originalPublicProviderIds, DoubanProviderId);
        }

        private static void PreserveProviderId(Movie movie, IReadOnlyDictionary<string, string> originalProviderIds, string providerIdKey)
        {
            if (originalProviderIds.TryGetValue(providerIdKey, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                movie.SetProviderId(providerIdKey, value);
            }
        }

        private static bool HasCompleteLlmConfiguration(Configuration.PluginConfiguration configuration)
        {
            return configuration.EnableLlmAssist
                && !string.IsNullOrWhiteSpace(configuration.LlmBaseUrl)
                && !string.IsNullOrWhiteSpace(configuration.LlmModel)
                && !string.IsNullOrWhiteSpace(configuration.LlmApiKey);
        }

        private static bool ShouldPreferLlmMovieSearchHints(MovieInfo info, string fileName, LlmSearchHints searchHints)
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

        private static MovieInfo CreateSafeLlmMovieLookupInfo(MovieInfo info)
        {
            var safePath = Config.LlmAllowRelativePathContext
                ? LlmRelativePathSanitizer.Sanitize(info.Path, Array.Empty<string?>(), nameof(Movie))
                : string.Empty;

            return new MovieInfo
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

        private static MovieInfo CreateSafeLlmExternalIdLookupInfo(MovieInfo info)
        {
            var safePath = Config.LlmAllowRelativePathContext
                ? LlmRelativePathSanitizer.Sanitize(info.Path, Array.Empty<string?>(), nameof(Movie))
                : string.Empty;

            return new MovieInfo
            {
                Name = info.Name,
                Path = safePath,
                MetadataLanguage = info.MetadataLanguage,
                MetadataCountryCode = info.MetadataCountryCode,
                Year = info.Year,
                ParentIndexNumber = info.ParentIndexNumber,
                IndexNumber = info.IndexNumber,
                IsAutomated = info.IsAutomated,
                ProviderIds = info.ProviderIds == null ? null : new Dictionary<string, string>(info.ProviderIds, StringComparer.OrdinalIgnoreCase),
            };
        }

        private static MovieInfo CreateSafeLlmTmdbCorrectionLookupInfo(MovieInfo info)
        {
            var safePath = Config.LlmAllowRelativePathContext
                ? LlmRelativePathSanitizer.Sanitize(info.Path, Array.Empty<string?>(), nameof(Movie))
                : string.Empty;

            return new MovieInfo
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

        private static string? GetProviderIdWriteValue(LlmExternalIdResolutionResult result, string providerIdKey)
        {
            return result.ProviderIdWrites
                .Where(write => IsMovieProviderIdWriteAllowed(write) && string.Equals(write.ProviderIdKey, providerIdKey, StringComparison.OrdinalIgnoreCase))
                .Select(write => write.ProviderIdValue)
                .FirstOrDefault();
        }

        private static void ApplyLlmExternalProviderIdWrites(MetadataResult<Movie> result, LlmExternalIdResolutionResult externalIdResolutionResult)
        {
            if (result.Item == null)
            {
                return;
            }

            ApplyLlmExternalProviderIdWrites(result.Item.ProviderIds, externalIdResolutionResult);
        }

        private static void ApplyLlmExternalProviderIdWrites(Dictionary<string, string> providerIds, LlmExternalIdResolutionResult externalIdResolutionResult)
        {
            foreach (var write in externalIdResolutionResult.ProviderIdWrites.Where(IsMovieProviderIdWriteAllowed))
            {
                if (providerIds.TryGetValue(write.ProviderIdKey, out var existingValue) && !string.IsNullOrWhiteSpace(existingValue))
                {
                    continue;
                }

                providerIds[write.ProviderIdKey] = write.ProviderIdValue;
            }
        }

        private static bool IsMovieProviderIdWriteAllowed(LlmExternalIdProviderIdWrite write)
        {
            return string.Equals(write.MediaType, nameof(Movie), StringComparison.Ordinal)
                && !string.Equals(write.ProviderIdKey, MetadataProvider.Tvdb.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, string>? CreateProviderIdPresenceOnlyCopy(Dictionary<string, string>? providerIds)
        {
            return providerIds?.Keys.ToDictionary(key => key, _ => "present", StringComparer.OrdinalIgnoreCase);
        }

        private async Task<LlmExternalIdResolutionResult> TryResolveMissingMovieProviderIdsWithLlmAsync(MovieInfo info, DefaultScraperSemantic semantic, CancellationToken cancellationToken)
        {
            if (this.llmExternalIdResolutionService == null || !HasCompleteLlmConfiguration(Config))
            {
                return LlmExternalIdResolutionResult.NotTriggered("LlmConfigurationMissing");
            }

            var triggerDecision = this.llmAssistTriggerPolicy.Evaluate(new LlmAssistTriggerContext
            {
                Configuration = Config,
                Semantic = semantic,
                MediaType = nameof(Movie),
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
                    LookupInfo = CreateSafeLlmExternalIdLookupInfo(info),
                    MediaType = nameof(Movie),
                    Name = info.Name,
                    Year = info.Year,
                    Semantic = semantic,
                    IsImageProvider = false,
                    HttpContext = this.HttpContextAccessor.HttpContext,
                    LibraryRoots = Array.Empty<string?>(),
                },
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<LlmTmdbIdCorrectionResult> TryResolveMovieTmdbCorrectionAsync(MovieInfo info, DefaultScraperSemantic semantic, string? originalTmdbId, CancellationToken cancellationToken)
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
                MediaType = nameof(Movie),
                IsImageProvider = false,
                HttpContext = this.HttpContextAccessor.HttpContext,
            });
            if (!triggerDecision.ShouldTrigger)
            {
                return LlmTmdbIdCorrectionResult.NoReplacement(triggerDecision.Reason);
            }

            var lookupInfo = CreateSafeLlmTmdbCorrectionLookupInfo(info);
            var relativePathSamples = string.IsNullOrWhiteSpace(lookupInfo.Path)
                ? Array.Empty<string?>()
                : new[] { lookupInfo.Path };

            return await this.llmExternalIdResolutionService.TryResolveTmdbCorrectionAsync(
                new LlmTmdbIdCorrectionRequest
                {
                    Configuration = Config,
                    LookupInfo = lookupInfo,
                    MediaType = nameof(Movie),
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

        private async Task<LlmScrapingAssistResult> TryAssistMovieMetadataWithLlmAsync(MovieInfo info, DefaultScraperSemantic semantic, CancellationToken cancellationToken)
        {
            if (this.llmMetadataAssistService == null || !Config.LlmAllowTextCompletion || !HasCompleteLlmConfiguration(Config))
            {
                return LlmScrapingAssistResult.NotTriggered("LlmConfigurationMissing");
            }

            var triggerDecision = this.llmAssistTriggerPolicy.Evaluate(new LlmAssistTriggerContext
            {
                Configuration = Config,
                Semantic = semantic,
                MediaType = nameof(Movie),
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
                    LookupInfo = CreateSafeLlmMovieLookupInfo(info),
                    MediaType = nameof(Movie),
                    Semantic = semantic,
                    IsImageProvider = false,
                    HttpContext = this.HttpContextAccessor.HttpContext,
                    LibraryRoots = Array.Empty<string?>(),
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
            this.Log("使用 LLM 提示搜索 Douban 电影. title: {0} year: {1}", title, year);
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
                ? results.FirstOrDefault(x => x.Category == "电影" && x.Year == year)
                : results.FirstOrDefault(x => x.Category == "电影");
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

            this.Log("使用 LLM 提示搜索 TMDb 电影. title: {0} year: {1}", title, searchHints.Year);
            return await this.GuestByTmdbAsync(title, searchHints.Year, info, cancellationToken).ConfigureAwait(false);
        }

        private async Task<(DoubanSubject? Subject, LlmScrapingAssistResult AssistResult)> TryCorrectDoubanMismatchWithLlmAsync(DoubanSubject subject, MovieInfo info, DefaultScraperSemantic semantic, LlmScrapingAssistResult existingAssistResult, CancellationToken cancellationToken)
        {
            if (existingAssistResult.Triggered || this.llmMetadataAssistService == null || !HasCompleteLlmConfiguration(Config))
            {
                return (null, existingAssistResult);
            }

            var context = this.llmScrapeContextBuilder.Build(CreateSafeLlmMovieLookupInfo(info), nameof(Movie), Array.Empty<string?>());
            var mismatch = this.llmScrapeMismatchDetector.Detect(context, new LlmScrapingSuggestion
            {
                MediaType = nameof(Movie),
                Title = subject.Name,
                OriginalTitle = subject.OriginalName,
                Year = subject.Year > 0 ? subject.Year : null,
                Confidence = 1,
            });
            if (!mismatch.IsMismatch)
            {
                return (null, existingAssistResult);
            }

            var assistResult = await this.TryAssistMovieMetadataWithLlmAsync(info, semantic, cancellationToken).ConfigureAwait(false);
            var correctedSid = await this.GuessByDoubanWithLlmHintsAsync(assistResult.SearchHints, info, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(correctedSid) || string.Equals(correctedSid, subject.Sid, StringComparison.OrdinalIgnoreCase))
            {
                return (null, assistResult);
            }

            return (await this.DoubanApi.GetMovieAsync(correctedSid, cancellationToken).ConfigureAwait(false), assistResult);
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

        private static bool IsSkippableMovieExtra(ParseNameResult parseResult)
        {
            return parseResult.IsExtra
                && !string.Equals(parseResult.AnimeType, "MOVIE", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<MetadataResult<Movie>> GetMetadataByTmdb(string tmdbId, MovieInfo info, CancellationToken cancellationToken)
        {
            this.Log("通过 TMDb 获取电影元数据. tmdbId: \"{0}\"", tmdbId);
            var result = new MetadataResult<Movie>();
            var movieResult = await this.TmdbApi
                            .GetMovieAsync(Convert.ToInt32(tmdbId, CultureInfo.InvariantCulture), info.MetadataLanguage, info.MetadataLanguage, cancellationToken)
                            .ConfigureAwait(false);

            if (movieResult == null)
            {
                return result;
            }

            var movie = new Movie
            {
                Name = movieResult.Title ?? movieResult.OriginalTitle,
                OriginalTitle = movieResult.OriginalTitle,
                Overview = movieResult.Overview?.Replace("\n\n", "\n", StringComparison.InvariantCulture),
                Tagline = movieResult.Tagline,
                ProductionLocations = movieResult.ProductionCountries.Select(pc => pc.Name).ToArray(),
            };

            if (Config.EnableTmdbTags && movieResult.Keywords?.Keywords != null)
            {
                var tagCount = movieResult.Keywords.Keywords.Count;
                for (var i = 0; i < movieResult.Keywords.Keywords.Count; i++)
                {
                    movie.AddTag(movieResult.Keywords.Keywords[i].Name);
                }

                if (tagCount > 0)
                {
                    this.Log("已写入电影 TMDb 标签. id={0} name={1} count={2}", movieResult.Id, movieResult.Title ?? movieResult.OriginalTitle, tagCount);
                }
            }

            result = new MetadataResult<Movie>
            {
                QueriedById = true,
                HasMetadata = true,
                ResultLanguage = info.MetadataLanguage,
                Item = movie,
            };

            movie.SetProviderId(MetadataProvider.Tmdb, tmdbId);
            if (!string.IsNullOrWhiteSpace(movieResult.ImdbId))
            {
                movie.SetProviderId(MetadataProvider.Imdb, movieResult.ImdbId);
            }

            // 这里 MetaSharkPlugin.ProviderId 的值做这么复杂，是为了保持唯一
            movie.SetProviderId(MetaSharkPlugin.ProviderId, $"{MetaSource.Tmdb}_{tmdbId}");

            // 获取电影系列信息
            if (movieResult.BelongsToCollection != null)
            {
                movie.CollectionName = movieResult.BelongsToCollection.Name;
            }

            movie.CommunityRating = (float)System.Math.Round(movieResult.VoteAverage, 2);
            movie.OfficialRating = this.GetTmdbOfficialRatingByData(movieResult, info.MetadataCountryCode);
            movie.PremiereDate = movieResult.ReleaseDate;
            movie.ProductionYear = movieResult.ReleaseDate?.Year;

            if (movieResult.ProductionCompanies != null)
            {
                movie.SetStudios(movieResult.ProductionCompanies.Select(c => c.Name));
            }

            var genres = movieResult.Genres;

            foreach (var genre in genres.Select(g => g.Name))
            {
                movie.AddGenre(genre);
            }

            var acceptedPeopleCount = await this.AddTmdbPeopleAsync(movieResult, result, cancellationToken).ConfigureAwait(false);
            this.TryQueueSearchMissingMetadataOverwriteCandidate(info, tmdbId, result.People, acceptedPeopleCount);

            return result;
        }

        private async Task<int> TryAddTmdbPeopleAsync(string tmdbId, MovieInfo info, MetadataResult<Movie> result, CancellationToken cancellationToken)
        {
            var movieResult = await this.TmdbApi
                .GetMovieAsync(Convert.ToInt32(tmdbId, CultureInfo.InvariantCulture), info.MetadataLanguage, info.MetadataLanguage, cancellationToken)
                .ConfigureAwait(false);

            if (movieResult == null)
            {
                return 0;
            }

            return await this.AddTmdbPeopleAsync(movieResult, result, cancellationToken).ConfigureAwait(false);
        }

        private void ApplyLlmTextCompletion(MetadataResult<Movie> result, LlmScrapingAssistResult llmAssistResult)
        {
            if (result.Item == null || !result.HasMetadata || llmAssistResult.Status != LlmScrapingAssistStatus.Succeeded)
            {
                return;
            }

            _ = this.llmMetadataMergePolicy.Apply(result, llmAssistResult.Suggestion, Config);
        }

        private MetadataResult<Movie>? HandleExtraType(MovieInfo info)
        {
            // 特典或extra视频可能和正片放在同一目录
            // TODO：插件暂时不支持设置影片为extra类型，只能直接忽略处理（最好放extras目录）
            var fileName = Path.GetFileNameWithoutExtension(info.Path) ?? info.Name;
            var parseResult = NameParser.Parse(fileName);
            if (IsSkippableMovieExtra(parseResult))
            {
                this.Log("识别到影片特典，跳过处理. name: {0}", fileName);
                return new MetadataResult<Movie>();
            }

            // 动画常用特典文件夹
            if (NameParser.IsSpecialDirectory(info.Path) || NameParser.IsExtraDirectory(info.Path))
            {
                this.Log("识别到影片特典，跳过处理. name: {0}", fileName);
                return new MetadataResult<Movie>();
            }

            return null;
        }

        private void TryQueueSearchMissingMetadataOverwriteCandidate(MovieInfo info, string tmdbId, IEnumerable<PersonInfo>? authoritativePeople, int expectedPeopleCount)
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

            var movie = !string.IsNullOrWhiteSpace(info.Path)
                ? this.LibraryManager.FindByPath(info.Path, false) as Movie
                : null;
            var authoritativePeopleSnapshot = CreateTmdbAuthoritativePeopleSnapshot(nameof(Movie), tmdbId, authoritativePeople);
            if (authoritativePeopleSnapshot == null
                || !RequiresSearchMissingMetadataOverwriteCandidate(movie, authoritativePeopleSnapshot))
            {
                return;
            }

            var itemId = movie?.Id ?? Guid.Empty;
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
                ItemPath = movie?.Path ?? info.Path ?? string.Empty,
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

        private async Task<int> AddTmdbPeopleAsync(TMDbLib.Objects.Movies.Movie movieResult, MetadataResult<Movie> result, CancellationToken cancellationToken)
        {
            var people = await this.GetPersonsAsync(movieResult, cancellationToken).ConfigureAwait(false);
            foreach (var person in people)
            {
                result.AddPerson(person);
            }

            return people.Count;
        }

        private async Task<IReadOnlyList<PersonInfo>> GetPersonsAsync(TMDbLib.Objects.Movies.Movie item, CancellationToken cancellationToken)
        {
            var persons = new List<PersonInfo>();

            // 演员
            if (item.Credits?.Cast != null)
            {
                var acceptedActorCount = 0;
                foreach (var actor in item.Credits.Cast.OrderBy(a => a.Order))
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

            // 导演
            if (item.Credits?.Crew != null)
            {
                var keepTypes = new[]
                {
                    PersonType.Director,
                    PersonType.Writer,
                    PersonType.Producer,
                };

                foreach (var person in item.Credits.Crew)
                {
                    // Normalize this
                    var type = MapCrewToPersonType(person);

                    if (!keepTypes.Contains(type, StringComparer.OrdinalIgnoreCase) &&
                            !keepTypes.Contains(person.Job ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var localizedName = await this.ResolveSimplifiedChineseOnlyItemPersonNameAsync(person.Name, person.Id, cancellationToken).ConfigureAwait(false);
                    if (localizedName == null)
                    {
                        continue;
                    }

                    var personInfo = new PersonInfo
                    {
                        Name = localizedName,
                        Role = person.Job,
                        Type = type == PersonType.Director ? PersonKind.Director : (type == PersonType.Producer ? PersonKind.Producer : PersonKind.Actor),
                    };

                    if (!string.IsNullOrWhiteSpace(person.ProfilePath))
                    {
                        personInfo.ImageUrl = this.TmdbApi.GetPosterUrl(person.ProfilePath)?.ToString();
                    }

                    if (person.Id > 0)
                    {
                        personInfo.SetProviderId(MetadataProvider.Tmdb, person.Id.ToString(CultureInfo.InvariantCulture));
                    }

                    persons.Add(personInfo);
                }
            }

            return persons;
        }

        private string? GetTmdbOfficialRatingByData(TMDbLib.Objects.Movies.Movie? movieResult, string preferredCountryCode)
        {
            _ = this.Logger;
            if (movieResult == null || movieResult.Releases?.Countries == null)
            {
                return null;
            }

            var releases = movieResult.Releases.Countries.Where(i => !string.IsNullOrWhiteSpace(i.Certification)).ToList();

            var ourRelease = releases.FirstOrDefault(c => string.Equals(c.Iso_3166_1, preferredCountryCode, StringComparison.OrdinalIgnoreCase));
            var usRelease = releases.FirstOrDefault(c => string.Equals(c.Iso_3166_1, "US", StringComparison.OrdinalIgnoreCase));
            var minimumRelease = releases.FirstOrDefault();

            if (ourRelease != null)
            {
                var ratingPrefix = string.Equals(preferredCountryCode, "us", StringComparison.OrdinalIgnoreCase) ? string.Empty : preferredCountryCode + "-";
                var newRating = ratingPrefix + ourRelease.Certification;

                newRating = newRating.Replace("de-", "FSK-", StringComparison.OrdinalIgnoreCase);

                return newRating;
            }

            if (usRelease != null)
            {
                return usRelease.Certification;
            }

            return minimumRelease?.Certification;
        }

        private async Task<SearchCollection?> GetTmdbCollection(MovieInfo info, string tmdbId, CancellationToken cancellationToken)
        {
            var movieResult = await this.TmdbApi
                            .GetMovieAsync(Convert.ToInt32(tmdbId, CultureInfo.InvariantCulture), info.MetadataLanguage, info.MetadataLanguage, cancellationToken)
                            .ConfigureAwait(false);
            if (movieResult != null)
            {
                return movieResult.BelongsToCollection;
            }

            return null;
        }

        private async Task<string?> GetTmdbOfficialRating(ItemLookupInfo info, string tmdbId, CancellationToken cancellationToken)
        {
            var movieResult = await this.TmdbApi
                            .GetMovieAsync(Convert.ToInt32(tmdbId, CultureInfo.InvariantCulture), info.MetadataLanguage, info.MetadataLanguage, cancellationToken)
                            .ConfigureAwait(false);

            return this.GetTmdbOfficialRatingByData(movieResult, info.MetadataCountryCode);
        }
    }
}
