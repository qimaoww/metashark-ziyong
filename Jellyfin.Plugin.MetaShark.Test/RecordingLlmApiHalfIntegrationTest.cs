using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.MetaShark;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Providers.Llm;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using TMDbLib.Objects.General;
using TMDbLib.Objects.Search;
using TMDbLib.Objects.TvShows;
using ContentRating = TMDbLib.Objects.TvShows.ContentRating;
using ResultContainer = TMDbLib.Objects.General.ResultContainer<TMDbLib.Objects.TvShows.ContentRating>;
using TmdbMovie = TMDbLib.Objects.Movies.Movie;
using TmdbSeries = TMDbLib.Objects.TvShows.TvShow;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [DoNotParallelize]
    public class RecordingLlmApiHalfIntegrationTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-recording-llm-api-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");

        [TestInitialize]
        public void ResetConfigurationBeforeTest()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(new PluginConfiguration());
        }

        [TestCleanup]
        public void ResetConfigurationAfterTest()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(new PluginConfiguration());
        }

        [TestMethod]
        public async Task RecordingLlmApi_MovieManualMatch_RecordsOneBackendCall()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = RecordingLoggerFactory.Create();
            var doubanApi = new DoubanApi(loggerFactory);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovie(tmdbApi, 9101, "zh-CN", CreateTmdbMovie(9101, "Recording Manual Movie", 2026));
            var recordingApi = RecordingLlmApi.ForMetadataSuggestion("Movie", "Recording Manual Movie", 2026, "ManualMatch");
            var provider = CreateMovieProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmApi: recordingApi);

            var info = CreateMovieInfo("Wrong Manual Movie", "Movies/Recording Manual Movie/Recording Manual Movie.mkv", 2026);
            info.ProviderIds = new Dictionary<string, string>
            {
                [MetadataProvider.Tmdb.ToString()] = "9101",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            recordingApi.ExpectOneBackendCall(LlmResponseSchemaKind.MetadataSuggestions, "Movie", "ManualMatch");
            recordingApi.AssertNoSensitiveLogFields();
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("9101", result.Item!.GetProviderId(MetadataProvider.Tmdb));
        }

        [TestMethod]
        public async Task RecordingLlmApi_SeriesManualMatch_RecordsOneBackendCall()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = RecordingLoggerFactory.Create();
            var doubanApi = new DoubanApi(loggerFactory);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 9201, "zh-CN", CreateTmdbSeries(9201, "Recording Manual Series"));
            var recordingApi = RecordingLlmApi.ForMetadataSuggestion("Series", "Recording Manual Series", 2027, "ManualMatch");
            var provider = CreateSeriesProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmApi: recordingApi);

            var info = CreateSeriesInfo("Wrong Manual Series", "Shows/Recording Manual Series");
            info.ProviderIds[MetadataProvider.Tmdb.ToString()] = "9201";

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            recordingApi.ExpectOneBackendCall(LlmResponseSchemaKind.MetadataSuggestions, "Series", "ManualMatch");
            recordingApi.AssertNoSensitiveLogFields();
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("9201", result.Item!.GetProviderId(MetadataProvider.Tmdb));
        }

        [TestMethod]
        public async Task RecordingLlmApi_SeasonManualMatch_RecordsOneBackendCall()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = RecordingLoggerFactory.Create();
            var recordingApi = RecordingLlmApi.ForMetadataSuggestion("Season", "Recording Manual Season", 2028, "ManualMatch", overview: "Recording season overview", seasonNumber: 2);
            var provider = CreateSeasonProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                llmApi: recordingApi);
            var info = CreateSeasonInfo("Recording Manual Season", "Shows/Recording Manual Season/Season 02", 2);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            recordingApi.ExpectOneBackendCall(LlmResponseSchemaKind.MetadataSuggestions, "Season", "ManualMatch");
            recordingApi.AssertNoSensitiveLogFields();
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("Recording Manual Season", result.Item!.Name);
            Assert.AreEqual("Recording season overview", result.Item.Overview);
            Assert.AreEqual(2, result.Item.IndexNumber);
        }

        [TestMethod]
        public async Task RecordingLlmApi_EpisodeManualMatch_RecordsOneBackendCall()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = RecordingLoggerFactory.Create();
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbEpisode(tmdbApi, 123, 1, 1, "zh-CN", CreateTmdbEpisode("第 1 集", null));
            var recordingApi = RecordingLlmApi.ForMetadataSuggestion("Episode", "Recording Manual Episode", 2028, "ManualMatch", overview: "Recording episode overview", seasonNumber: 1, episodeNumber: 1);
            var provider = CreateEpisodeProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                tmdbApi: tmdbApi,
                llmApi: recordingApi);
            var info = CreateEpisodeInfo("第 1 集", "Shows/Recording Manual Episode/Season 01/S01E01.mkv");

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            recordingApi.ExpectOneBackendCall(LlmResponseSchemaKind.MetadataSuggestions, "Episode", "ManualMatch");
            recordingApi.AssertNoSensitiveLogFields();
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("Recording Manual Episode", result.Item!.Name);
            Assert.AreEqual("Recording episode overview", result.Item.Overview);
            Assert.AreEqual(1, result.Item.ParentIndexNumber);
            Assert.AreEqual(1, result.Item.IndexNumber);
        }

        [TestMethod]
        public async Task RecordingLlmApi_MovieTextCompletionDisabled_RecordsNoBackendCallWithReason()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration(allowTextCompletion: false));
            using var loggerFactory = RecordingLoggerFactory.Create();
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovie(tmdbApi, 9301, "zh-CN", CreateTmdbMovie(9301, "Disabled Text Movie", 2029));
            var recordingApi = RecordingLlmApi.ForMetadataSuggestion("Movie", "Should Not Call Movie", 2029, "ManualMatch");
            var provider = CreateMovieProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                tmdbApi: tmdbApi,
                llmApi: recordingApi);
            var info = CreateMovieInfo("Disabled Text Movie", "Movies/Disabled Text Movie/Disabled Text Movie.mkv", 2029);
            info.ProviderIds = new Dictionary<string, string>
            {
                [MetadataProvider.Tmdb.ToString()] = "9301",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            recordingApi.ExpectNoBackendCall("TextCompletionDisabled");
            loggerFactory.ExpectRejectedReason("TextCompletionDisabled", "Movie");
            recordingApi.AssertNoSensitiveLogFields();
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("Disabled Text Movie overview", result.Item!.Overview);
        }

        [TestMethod]
        public async Task RecordingLlmApi_SeriesTextCompletionDisabled_RecordsNoBackendCallWithReason()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration(allowTextCompletion: false));
            using var loggerFactory = RecordingLoggerFactory.Create();
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 9401, "zh-CN", CreateTmdbSeries(9401, "Disabled Text Series"));
            var recordingApi = RecordingLlmApi.ForMetadataSuggestion("Series", "Should Not Call Series", 2029, "ManualMatch");
            var provider = CreateSeriesProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                tmdbApi: tmdbApi,
                llmApi: recordingApi);
            var info = CreateSeriesInfo("Disabled Text Series", "Shows/Disabled Text Series");
            info.ProviderIds[MetadataProvider.Tmdb.ToString()] = "9401";

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            recordingApi.ExpectNoBackendCall("TextCompletionDisabled");
            loggerFactory.ExpectRejectedReason("TextCompletionDisabled", "Series");
            recordingApi.AssertNoSensitiveLogFields();
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("Disabled Text Series overview", result.Item!.Overview);
        }

        [TestMethod]
        public async Task RecordingLlmApi_SeasonTextCompletionDisabled_RecordsNoBackendCallWithReason()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration(allowTextCompletion: false));
            using var loggerFactory = RecordingLoggerFactory.Create();
            var recordingApi = RecordingLlmApi.ForMetadataSuggestion("Season", "Should Not Call Season", 2029, "ManualMatch", seasonNumber: 2);
            var provider = CreateSeasonProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                llmApi: recordingApi);
            var info = CreateSeasonInfo("Disabled Text Season", "Shows/Disabled Text Season/Season 02", 2);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            recordingApi.ExpectNoBackendCall("TextCompletionDisabled");
            loggerFactory.ExpectRejectedReason("TextCompletionDisabled", "Season");
            recordingApi.AssertNoSensitiveLogFields();
            Assert.IsFalse(result.HasMetadata);
        }

        [TestMethod]
        public async Task RecordingLlmApi_EpisodeTextCompletionDisabled_RecordsNoBackendCallWithReason()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration(allowTextCompletion: false));
            using var loggerFactory = RecordingLoggerFactory.Create();
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbEpisode(tmdbApi, 123, 1, 1, "zh-CN", CreateTmdbEpisode("第 1 集", null));
            var recordingApi = RecordingLlmApi.ForMetadataSuggestion("Episode", "Should Not Call Episode", 2029, "ManualMatch", seasonNumber: 1, episodeNumber: 1);
            var provider = CreateEpisodeProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                tmdbApi: tmdbApi,
                llmApi: recordingApi);
            var info = CreateEpisodeInfo("第 1 集", "Shows/Disabled Text Episode/Season 01/S01E01.mkv");

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            recordingApi.ExpectNoBackendCall("TextCompletionDisabled");
            loggerFactory.ExpectRejectedReason("TextCompletionDisabled", "Episode");
            recordingApi.AssertNoSensitiveLogFields();
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("第 1 集", result.Item!.Name);
            Assert.IsNull(result.Item.Overview);
        }

        [TestMethod]
        public async Task RecordingLlmApi_MovieAutomaticRefresh_RecordsNoBackendCallWithReason()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = RecordingLoggerFactory.Create();
            var recordingApi = RecordingLlmApi.ForMetadataSuggestion("Movie", "Rejected Movie", 2028, "ManualMatch");
            var provider = CreateMovieProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateAutomaticRefreshContextAccessor(),
                llmApi: recordingApi);
            var info = CreateMovieInfo("Rejected Movie", "Movies/Rejected Movie/Rejected Movie.mkv", 2028);
            info.IsAutomated = true;

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            recordingApi.ExpectNoBackendCall("AutomaticRefreshRejected");
            loggerFactory.ExpectRejectedReason("AutomaticRefreshRejected", "Movie");
            recordingApi.AssertNoSensitiveLogFields();
            Assert.IsFalse(result.HasMetadata);
        }

        [TestMethod]
        public async Task RecordingLlmApi_MovieImplicitRefresh_RecordsNoBackendCallWithReason()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = RecordingLoggerFactory.Create();
            var recordingApi = RecordingLlmApi.ForMetadataSuggestion("Movie", "Implicit Rejected Movie", 2029, "ManualMatch");
            var provider = CreateMovieProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateAutomaticRefreshContextAccessor(),
                llmApi: recordingApi);
            var info = CreateMovieInfo("Implicit Rejected Movie", "Movies/Implicit Rejected Movie/Implicit Rejected Movie.mkv", 2029);
            info.IsAutomated = false;

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            recordingApi.ExpectNoBackendCall("ImplicitRefreshRejected");
            loggerFactory.ExpectRejectedReason("ImplicitRefreshRejected", "Movie");
            recordingApi.AssertNoSensitiveLogFields();
            Assert.IsFalse(result.HasMetadata);
        }

        [TestMethod]
        public void RecordingLlmApi_StaleExternalIdConflict_ObservabilityNormalization_UsesPublicReasonCode()
        {
            using var loggerFactory = RecordingLoggerFactory.Create();
            LlmObservabilityLog.LogTmdbCorrectionRejected(
                loggerFactory.CreateLogger(typeof(LlmExternalIdResolutionService).FullName ?? nameof(LlmExternalIdResolutionService)),
                "ImdbEvidenceDoesNotAlign",
                "Movie",
                DefaultScraperSemantic.UserRefresh,
                false);

            loggerFactory.ExpectReasonCode("StaleExternalIdConflict", "Movie");
        }

        [TestMethod]
        public void RecordingLlmApi_TmdbCorrection_FailClosedReasons_UseStablePublicReasonCodes()
        {
            using var loggerFactory = RecordingLoggerFactory.Create();
            var logger = loggerFactory.CreateLogger(typeof(LlmExternalIdResolutionService).FullName ?? nameof(LlmExternalIdResolutionService));

            LlmObservabilityLog.LogTmdbCorrectionRejected(logger, "AmbiguousCandidates", "Movie", DefaultScraperSemantic.UserRefresh, false);
            LlmObservabilityLog.LogTmdbCorrectionRejected(logger, "CandidateVerificationFailed", "Movie", DefaultScraperSemantic.UserRefresh, false);
            LlmObservabilityLog.LogTmdbCorrectionRejected(logger, "EvidenceVerificationFailed", "Movie", DefaultScraperSemantic.UserRefresh, false);
            LlmObservabilityLog.LogTmdbCorrectionRejected(logger, "NoTmdbCandidate", "Movie", DefaultScraperSemantic.UserRefresh, false);

            loggerFactory.ExpectRejectedReason("AmbiguousCandidates", "Movie", "TmdbCorrection.Rejected");
            loggerFactory.ExpectRejectedReason("CandidateVerificationFailed", "Movie", "TmdbCorrection.Rejected");
            loggerFactory.ExpectRejectedReason("EvidenceVerificationFailed", "Movie", "TmdbCorrection.Rejected");
            loggerFactory.ExpectRejectedReason("NoTmdbCandidate", "Movie", "TmdbCorrection.Rejected");
        }

        [TestMethod]
        public void RecordingLlmApi_TmdbCorrection_ExplicitSearchMissingRefreshTrigger_RecordsAcceptedReason()
        {
            using var loggerFactory = RecordingLoggerFactory.Create();
            var policy = new LlmTmdbIdCorrectionTriggerPolicy(loggerFactory.CreateLogger<LlmTmdbIdCorrectionTriggerPolicy>());
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Method = HttpMethods.Post;
            httpContext.Request.Path = "/Items/11111111-1111-1111-1111-111111111111/Refresh";
            httpContext.Request.QueryString = new QueryString("?metadataRefreshMode=FullRefresh&replaceAllMetadata=false");

            var decision = policy.Evaluate(new LlmAssistTriggerContext
            {
                Configuration = new PluginConfiguration
                {
                    EnableLlmAssist = true,
                    EnableLlmTmdbIdCorrection = true,
                    LlmBaseUrl = "http://127.0.0.1:11434/v1",
                    LlmModel = "test-model",
                    LlmApiKey = "sk-test-secret",
                    LlmAllowTextCompletion = true,
                    LlmConfidenceThreshold = 0.75,
                },
                Semantic = DefaultScraperSemantic.UserRefresh,
                MediaType = "Movie",
                IsImageProvider = false,
                HttpContext = httpContext,
            });

            Assert.IsTrue(decision.ShouldTrigger);
            Assert.AreEqual(LlmTmdbIdCorrectionTriggerPolicy.ExplicitSearchMissingMetadataRefreshReason, decision.Reason);
            loggerFactory.ExpectAcceptedReason(LlmTmdbIdCorrectionTriggerPolicy.ExplicitSearchMissingMetadataRefreshReason, "Movie", "TmdbCorrection.Evaluated");
        }

        [TestMethod]
        public void RecordingLlmApi_TmdbCorrection_Applied_RecordsAppliedReason()
        {
            using var loggerFactory = RecordingLoggerFactory.Create();
            LlmObservabilityLog.LogTmdbCorrectionApplied(
                loggerFactory.CreateLogger(typeof(LlmExternalIdResolutionService).FullName ?? nameof(LlmExternalIdResolutionService)),
                "StrongTmdbEvidenceMatched",
                "Series");

            loggerFactory.ExpectAppliedReason("StrongTmdbEvidenceMatched", "Series");
        }

        private static MovieProvider CreateMovieProvider(
            ILoggerFactory loggerFactory,
            IHttpContextAccessor httpContextAccessor,
            DoubanApi? doubanApi = null,
            TmdbApi? tmdbApi = null,
            RecordingLlmApi? llmApi = null)
        {
            var api = llmApi ?? RecordingLlmApi.ForMetadataSuggestion("Movie", "Fallback Movie", 2024, "ManualMatch");
            return new MovieProvider(
                new DefaultHttpClientFactory(),
                loggerFactory,
                new Mock<ILibraryManager>().Object,
                httpContextAccessor,
                doubanApi ?? new DoubanApi(loggerFactory),
                tmdbApi ?? new TmdbApi(loggerFactory),
                new OmdbApi(loggerFactory),
                new ImdbApi(loggerFactory),
                new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore(),
                CreateMetadataAssistService(api),
                CreateExternalIdResolutionService(api, loggerFactory, tmdbApi: tmdbApi, doubanApi: doubanApi));
        }

        private static SeriesProvider CreateSeriesProvider(
            ILoggerFactory loggerFactory,
            IHttpContextAccessor httpContextAccessor,
            DoubanApi? doubanApi = null,
            TmdbApi? tmdbApi = null,
            RecordingLlmApi? llmApi = null)
        {
            var api = llmApi ?? RecordingLlmApi.ForMetadataSuggestion("Series", "Fallback Series", 2024, "ManualMatch");
            return new SeriesProvider(
                new DefaultHttpClientFactory(),
                loggerFactory,
                new Mock<ILibraryManager>().Object,
                httpContextAccessor,
                doubanApi ?? new DoubanApi(loggerFactory),
                tmdbApi ?? new TmdbApi(loggerFactory),
                new OmdbApi(loggerFactory),
                new ImdbApi(loggerFactory),
                new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore(),
                CreateMetadataAssistService(api),
                null,
                CreateExternalIdResolutionService(api, loggerFactory, tmdbApi: tmdbApi, doubanApi: doubanApi));
        }

        private static SeasonProvider CreateSeasonProvider(
            ILoggerFactory loggerFactory,
            IHttpContextAccessor httpContextAccessor,
            DoubanApi? doubanApi = null,
            TmdbApi? tmdbApi = null,
            RecordingLlmApi? llmApi = null)
        {
            var api = llmApi ?? RecordingLlmApi.ForMetadataSuggestion("Season", "Fallback Season", 2024, "ManualMatch", seasonNumber: 1);
            return new SeasonProvider(
                new DefaultHttpClientFactory(),
                loggerFactory,
                new Mock<ILibraryManager>().Object,
                httpContextAccessor,
                doubanApi ?? new DoubanApi(loggerFactory),
                tmdbApi ?? new TmdbApi(loggerFactory),
                new OmdbApi(loggerFactory),
                new ImdbApi(loggerFactory),
                CreateMetadataAssistService(api));
        }

        private static EpisodeProvider CreateEpisodeProvider(
            ILoggerFactory loggerFactory,
            IHttpContextAccessor httpContextAccessor,
            TmdbApi? tmdbApi = null,
            RecordingLlmApi? llmApi = null)
        {
            var api = llmApi ?? RecordingLlmApi.ForMetadataSuggestion("Episode", "Fallback Episode", 2024, "ManualMatch", seasonNumber: 1, episodeNumber: 1);
            var libraryManager = new Mock<ILibraryManager>();
            return new EpisodeProvider(
                new DefaultHttpClientFactory(),
                loggerFactory,
                libraryManager.Object,
                httpContextAccessor,
                new DoubanApi(loggerFactory),
                tmdbApi ?? new TmdbApi(loggerFactory),
                new OmdbApi(loggerFactory),
                new ImdbApi(loggerFactory),
                new TvdbApi(loggerFactory),
                null,
                null,
                CreateMetadataAssistService(api));
        }

        private static LlmMetadataAssistService CreateMetadataAssistService(RecordingLlmApi api)
        {
            return new LlmMetadataAssistService(
                api,
                new LlmAssistTriggerPolicy(),
                new LlmScrapeContextBuilder(),
                new LlmSuggestionValidator(),
                new LlmScrapeMismatchDetector(),
                new LlmMetadataMergePolicy(),
                new LlmRequestLimiter());
        }

        private static LlmExternalIdResolutionService CreateExternalIdResolutionService(
            RecordingLlmApi api,
            ILoggerFactory loggerFactory,
            TmdbApi? tmdbApi = null,
            DoubanApi? doubanApi = null)
        {
            return new LlmExternalIdResolutionService(
                api,
                tmdbApi ?? new TmdbApi(loggerFactory),
                doubanApi ?? new DoubanApi(loggerFactory),
                new TvdbApi(loggerFactory),
                new LlmAssistTriggerPolicy(),
                new LlmExternalIdCandidateValidator(),
                new LlmRequestLimiter());
        }

        private static MovieInfo CreateMovieInfo(string name, string path, int year)
        {
            return new MovieInfo
            {
                Name = name,
                Path = path,
                Year = year,
                MetadataLanguage = "zh-CN",
                MetadataCountryCode = "CN",
                IsAutomated = false,
            };
        }

        private static SeriesInfo CreateSeriesInfo(string name, string path)
        {
            return new SeriesInfo
            {
                Name = name,
                Path = path,
                MetadataLanguage = "zh-CN",
                MetadataCountryCode = "CN",
                IsAutomated = false,
                ProviderIds = new Dictionary<string, string>
                {
                    [MetadataProvider.Tmdb.ToString()] = string.Empty,
                },
            };
        }

        private static SeasonInfo CreateSeasonInfo(string name, string path, int seasonNumber)
        {
            return new SeasonInfo
            {
                Name = name,
                Path = path,
                IndexNumber = seasonNumber,
                MetadataLanguage = "zh-CN",
                MetadataCountryCode = "CN",
                IsAutomated = false,
                ProviderIds = new Dictionary<string, string>(),
                SeriesProviderIds = new Dictionary<string, string>(),
            };
        }

        private static EpisodeInfo CreateEpisodeInfo(string name, string path)
        {
            return new EpisodeInfo
            {
                Name = name,
                Path = path,
                ParentIndexNumber = 1,
                IndexNumber = 1,
                MetadataLanguage = "zh-CN",
                MetadataCountryCode = "CN",
                IsAutomated = false,
                ProviderIds = new Dictionary<string, string>(),
                SeriesProviderIds = new Dictionary<string, string>
                {
                    [MetadataProvider.Tmdb.ToString()] = "123",
                },
            };
        }

        private static PluginConfiguration CreateLlmConfiguration(bool allowTextCompletion = true)
        {
            return new PluginConfiguration
            {
                EnableLlmAssist = true,
                LlmBaseUrl = "http://127.0.0.1:11434/v1",
                LlmModel = "test-model",
                LlmApiKey = "sk-test-secret",
                LlmAllowTextCompletion = allowTextCompletion,
                LlmConfidenceThreshold = 0.75,
            };
        }

        private static DoubanSubject CreateDoubanSubject(string sid, string name, int year, string category)
        {
            return new DoubanSubject
            {
                Sid = sid,
                Name = name,
                OriginalName = name,
                Year = year,
                Category = category,
                Rating = 8.5f,
                Genre = "剧情",
                Intro = name + "简介",
                Screen = $"{year.ToString(CultureInfo.InvariantCulture)}-01-01",
                Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p0000000001.webp",
            };
        }

        private static TmdbMovie CreateTmdbMovie(int tmdbId, string title, int year)
        {
            return new TmdbMovie
            {
                Id = tmdbId,
                Title = title,
                OriginalTitle = title,
                Overview = title + " overview",
                ReleaseDate = new DateTime(year, 1, 1),
                VoteAverage = 8.2,
                ProductionCountries = new List<ProductionCountry>(),
                Genres = new List<Genre>(),
                ProductionCompanies = new List<ProductionCompany>(),
            };
        }

        private static TmdbSeries CreateTmdbSeries(int tmdbId, string title)
        {
            return new TmdbSeries
            {
                Id = tmdbId,
                Name = title,
                OriginalName = title,
                Overview = title + " overview",
                FirstAirDate = new DateTime(2027, 1, 1),
                VoteAverage = 8.3,
                EpisodeRunTime = new List<int>(),
                ContentRatings = new ResultContainer
                {
                    Results = new List<ContentRating>(),
                },
                ExternalIds = new ExternalIdsTvShow(),
            };
        }

        private static TvEpisode CreateTmdbEpisode(string title, string? overview)
        {
            return new TvEpisode
            {
                Name = title,
                Overview = overview,
                VoteAverage = 8.4,
                AirDate = new DateTime(2028, 1, 1),
            };
        }

        private static void SeedTmdbMovie(TmdbApi tmdbApi, int tmdbId, string language, TmdbMovie movie)
        {
            GetTmdbMemoryCache(tmdbApi).Set($"movie-{tmdbId.ToString(CultureInfo.InvariantCulture)}-{language}-{language}", movie, TimeSpan.FromMinutes(5));
        }

        private static void SeedTmdbSeries(TmdbApi tmdbApi, int tmdbId, string language, TmdbSeries series)
        {
            GetTmdbMemoryCache(tmdbApi).Set($"series-{tmdbId.ToString(CultureInfo.InvariantCulture)}-{language}-{language}", series, TimeSpan.FromMinutes(5));
        }

        private static void SeedFindByExternalId(TmdbApi tmdbApi, string externalId, string language, int[]? movieIds = null, int[]? seriesIds = null)
        {
            GetTmdbMemoryCache(tmdbApi).Set(
                $"find-Imdb-{externalId}-{language}",
                new TMDbLib.Objects.Find.FindContainer
                {
                    MovieResults = (movieIds ?? Array.Empty<int>()).Select(id => new SearchMovie { Id = id }).ToList(),
                    TvResults = (seriesIds ?? Array.Empty<int>()).Select(id => new SearchTv { Id = id }).ToList(),
                },
                TimeSpan.FromMinutes(5));
        }

        private static string CandidateJson(string provider, string id, string mediaType, double confidence = 0.9, string reason = "title and year match", string evidence = "filename match")
        {
            return JsonSerializer.Serialize(new
            {
                provider,
                id,
                mediaType,
                confidence,
                reason,
                evidence,
            });
        }

        private static void SeedTmdbEpisode(TmdbApi tmdbApi, int seriesTmdbId, int seasonNumber, int episodeNumber, string language, TvEpisode episode)
        {
            GetTmdbMemoryCache(tmdbApi).Set($"episode-{seriesTmdbId.ToString(CultureInfo.InvariantCulture)}-s{seasonNumber.ToString(CultureInfo.InvariantCulture)}e{episodeNumber.ToString(CultureInfo.InvariantCulture)}-{language}-{language}", episode, TimeSpan.FromMinutes(5));
            GetTmdbMemoryCache(tmdbApi).Set<EpisodeLocalizedValue?>($"episode-translation-title-{seriesTmdbId.ToString(CultureInfo.InvariantCulture)}-s{seasonNumber.ToString(CultureInfo.InvariantCulture)}e{episodeNumber.ToString(CultureInfo.InvariantCulture)}-{language}", null, TimeSpan.FromMinutes(5));
            GetTmdbMemoryCache(tmdbApi).Set<EpisodeLocalizedValue?>($"episode-translation-overview-{seriesTmdbId.ToString(CultureInfo.InvariantCulture)}-s{seasonNumber.ToString(CultureInfo.InvariantCulture)}e{episodeNumber.ToString(CultureInfo.InvariantCulture)}-{language}", null, TimeSpan.FromMinutes(5));
        }

        private static void SeedDoubanSearchResults(DoubanApi doubanApi, string query, params DoubanSubject[] subjects)
        {
            GetDoubanMemoryCache(doubanApi).Set($"movie_search_{query}", subjects.ToList(), TimeSpan.FromMinutes(5));
        }

        private static void SeedDoubanSubject(DoubanApi doubanApi, DoubanSubject subject)
        {
            GetDoubanMemoryCache(doubanApi).Set($"movie_{subject.Sid}", subject, TimeSpan.FromMinutes(5));
        }

        private static MemoryCache GetDoubanMemoryCache(DoubanApi doubanApi)
        {
            var memoryCacheField = typeof(DoubanApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(memoryCacheField, "DoubanApi.memoryCache 未找到");
            var memoryCache = memoryCacheField!.GetValue(doubanApi) as MemoryCache;
            Assert.IsNotNull(memoryCache, "DoubanApi.memoryCache 不是有效的 MemoryCache");
            return memoryCache!;
        }

        private static MemoryCache GetTmdbMemoryCache(TmdbApi tmdbApi)
        {
            var memoryCacheField = typeof(TmdbApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(memoryCacheField, "TmdbApi.memoryCache 未找到");
            var memoryCache = memoryCacheField!.GetValue(tmdbApi) as MemoryCache;
            Assert.IsNotNull(memoryCache, "TmdbApi.memoryCache 不是有效的 MemoryCache");
            return memoryCache!;
        }

        private static void EnsurePluginInstance()
        {
            if (MetaSharkPlugin.Instance != null)
            {
                EnsurePluginConfiguration();
                return;
            }

            Directory.CreateDirectory(PluginsPath);
            Directory.CreateDirectory(PluginConfigurationsPath);

            var appHost = new Mock<IServerApplicationHost>();
            appHost.Setup(x => x.GetLocalApiUrl(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>())).Returns("http://127.0.0.1:8096");
            var applicationPaths = new Mock<IApplicationPaths>();
            applicationPaths.SetupGet(x => x.PluginsPath).Returns(PluginsPath);
            applicationPaths.SetupGet(x => x.PluginConfigurationsPath).Returns(PluginConfigurationsPath);
            var xmlSerializer = new Mock<IXmlSerializer>();

            _ = new MetaSharkPlugin(appHost.Object, applicationPaths.Object, xmlSerializer.Object);
            EnsurePluginConfiguration();
        }

        private static void ReplacePluginConfiguration(PluginConfiguration configuration)
        {
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);

            var currentType = plugin!.GetType();
            while (currentType != null)
            {
                var configurationProperty = currentType.GetProperty("Configuration", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (configurationProperty != null
                    && configurationProperty.PropertyType.IsAssignableFrom(typeof(PluginConfiguration))
                    && configurationProperty.SetMethod != null)
                {
                    configurationProperty.SetValue(plugin, configuration);
                    return;
                }

                var configurationField = currentType
                    .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .FirstOrDefault(field => field.FieldType.IsAssignableFrom(typeof(PluginConfiguration)));
                if (configurationField != null)
                {
                    configurationField.SetValue(plugin, configuration);
                    return;
                }

                currentType = currentType.BaseType;
            }

            Assert.Fail("Could not replace MetaSharkPlugin configuration for tests.");
        }

        private static void EnsurePluginConfiguration()
        {
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);

            if (plugin!.Configuration != null)
            {
                return;
            }

            ReplacePluginConfiguration(new PluginConfiguration());
        }

        private sealed class RecordingLlmApi : ILlmApi
        {
            private static readonly string[] ForbiddenSensitiveFragments =
            {
                "/mnt",
                "/root",
                "/home",
                "/opt",
                "C:\\",
                "\\\\",
                "sk-test-secret",
                "Bearer ",
                "http://127.0.0.1:11434",
            };

            private readonly Queue<string> metadataResponses = new Queue<string>();
            private readonly Queue<string> acceptedReasons = new Queue<string>();

            private RecordingLlmApi()
            {
            }

            public List<BackendCall> Calls { get; } = new List<BackendCall>();

            public static RecordingLlmApi ForMetadataSuggestion(string mediaType, string title, int year, string acceptedReason, string? overview = null, int? seasonNumber = null, int? episodeNumber = null)
            {
                var api = new RecordingLlmApi();
                api.acceptedReasons.Enqueue(acceptedReason);
                api.metadataResponses.Enqueue(JsonSerializer.Serialize(new
                {
                    suggestions = new[]
                    {
                        new
                        {
                            mediaType,
                            title,
                            year,
                            overview,
                            seasonNumber,
                            episodeNumber,
                            confidence = 0.96,
                        },
                    },
                }));
                return api;
            }

            public static RecordingLlmApi ForExternalIdCandidates(string acceptedReason, params string[] candidates)
            {
                var api = new RecordingLlmApi();
                api.acceptedReasons.Enqueue(acceptedReason);
                api.metadataResponses.Enqueue("{\"externalIdCandidates\":[" + string.Join(",", candidates) + "]}");
                return api;
            }

            public Task<LlmApiResult> CompleteAsync(string prompt, CancellationToken cancellationToken)
            {
                return this.CompleteAsync(prompt, LlmResponseSchemaKind.MetadataSuggestions, cancellationToken);
            }

            public Task<LlmApiResult> CompleteAsync(string prompt, LlmResponseSchemaKind responseSchemaKind, CancellationToken cancellationToken)
            {
                AssertNoSensitiveContent(prompt);
                this.Calls.Add(new BackendCall(responseSchemaKind, ExtractItemType(prompt), this.ReadAcceptedReason(responseSchemaKind)));

                var response = responseSchemaKind == LlmResponseSchemaKind.MetadataSuggestions && this.metadataResponses.Count > 0
                    ? this.metadataResponses.Dequeue()
                    : "{\"externalIdCandidates\":[]}";
                return Task.FromResult(LlmApiResult.Succeeded(response));
            }

            public void ExpectOneBackendCall(LlmResponseSchemaKind featureKind, string itemType, string reason)
            {
                Assert.AreEqual(1, this.Calls.Count, "Expected exactly one LLM backend boundary call.");
                var call = this.Calls[0];
                Assert.AreEqual(featureKind, call.FeatureKind);
                Assert.AreEqual(itemType, call.ItemType);
                Assert.AreEqual(reason, call.Reason);
            }

            public void ExpectNoBackendCall(string reason)
            {
                Assert.AreEqual(0, this.Calls.Count, $"Expected no LLM backend boundary calls; rejection reason must be observed separately: {reason}.");
            }

            public void AssertNoSensitiveLogFields()
            {
                foreach (var call in this.Calls)
                {
                    AssertNoSensitiveContent(call.ItemType);
                    AssertNoSensitiveContent(call.Reason);
                }
            }

            private static void AssertNoSensitiveContent(string? text)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                foreach (var forbidden in ForbiddenSensitiveFragments)
                {
                    Assert.IsFalse(text.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"Sensitive LLM boundary content leaked '{forbidden}': {text}");
                }
            }

            private static string ExtractItemType(string prompt)
            {
                using var document = JsonDocument.Parse(prompt);
                if (TryGetPropertyIgnoreCase(document.RootElement, "MediaType", out var mediaType) && mediaType.ValueKind == JsonValueKind.String)
                {
                    return mediaType.GetString() ?? string.Empty;
                }

                if (TryGetPropertyIgnoreCase(document.RootElement, "Context", out var context)
                    && context.ValueKind == JsonValueKind.Object
                    && TryGetPropertyIgnoreCase(context, "MediaType", out var contextMediaType)
                    && contextMediaType.ValueKind == JsonValueKind.String)
                {
                    return contextMediaType.GetString() ?? string.Empty;
                }

                return string.Empty;
            }

            private string ReadAcceptedReason(LlmResponseSchemaKind responseSchemaKind)
            {
                return this.acceptedReasons.Count > 0
                    ? this.acceptedReasons.Dequeue()
                    : responseSchemaKind.ToString();
            }

            private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }

                value = default;
                return false;
            }

            public sealed record BackendCall(LlmResponseSchemaKind FeatureKind, string ItemType, string Reason);
        }

        private sealed class RecordingLoggerFactory : ILoggerFactory
        {
            private readonly RecordingLoggerProvider provider = new RecordingLoggerProvider();

            private RecordingLoggerFactory()
            {
            }

            public static RecordingLoggerFactory Create()
            {
                return new RecordingLoggerFactory();
            }

            public void AddProvider(ILoggerProvider loggerProvider)
            {
                _ = loggerProvider;
            }

            public ILogger CreateLogger(string categoryName)
            {
                return this.provider.CreateLogger(categoryName);
            }

            public void Dispose()
            {
                this.provider.Dispose();
            }

            public void ExpectRejectedReason(string reasonCode, string mediaType, string eventName = "LlmAssistTrigger.Rejected")
            {
                Assert.IsTrue(
                    this.provider.Entries.Any(entry =>
                        entry.Level == LogLevel.Information
                        && string.Equals(entry.EventId.Name, eventName, StringComparison.Ordinal)
                        && entry.State.TryGetValue("ReasonCode", out var actualReason)
                        && string.Equals(actualReason?.ToString(), reasonCode, StringComparison.Ordinal)
                        && entry.State.TryGetValue("MediaType", out var actualMediaType)
                        && string.Equals(actualMediaType?.ToString(), mediaType, StringComparison.Ordinal)),
                    $"Expected captured LLM rejection reason {reasonCode} for {mediaType} on {eventName}.");
            }

            public void ExpectReasonCode(string reasonCode, string mediaType)
            {
                Assert.IsTrue(
                    this.provider.Entries.Any(entry =>
                        entry.Level == LogLevel.Information
                        && entry.State.TryGetValue("ReasonCode", out var actualReason)
                        && string.Equals(actualReason?.ToString(), reasonCode, StringComparison.Ordinal)
                        && entry.State.TryGetValue("MediaType", out var actualMediaType)
                        && string.Equals(actualMediaType?.ToString(), mediaType, StringComparison.Ordinal)),
                    $"Expected captured reason code {reasonCode} for {mediaType} in any observability event.");
            }

            public void ExpectAcceptedReason(string reasonCode, string mediaType, string eventName)
            {
                Assert.IsTrue(
                    this.provider.Entries.Any(entry =>
                        entry.Level == LogLevel.Information
                        && string.Equals(entry.EventId.Name, eventName, StringComparison.Ordinal)
                        && entry.State.TryGetValue("ReasonCode", out var actualReason)
                        && string.Equals(actualReason?.ToString(), reasonCode, StringComparison.Ordinal)
                        && entry.State.TryGetValue("Accepted", out var actualAccepted)
                        && string.Equals(actualAccepted?.ToString(), bool.TrueString, StringComparison.OrdinalIgnoreCase)
                        && entry.State.TryGetValue("MediaType", out var actualMediaType)
                        && string.Equals(actualMediaType?.ToString(), mediaType, StringComparison.Ordinal)),
                    $"Expected accepted reason {reasonCode} for {mediaType} on {eventName}.");
            }

            public void ExpectAppliedReason(string reasonCode, string mediaType)
            {
                Assert.IsTrue(
                    this.provider.Entries.Any(entry =>
                        entry.Level == LogLevel.Information
                        && string.Equals(entry.EventId.Name, "TmdbCorrection.Applied", StringComparison.Ordinal)
                        && entry.State.TryGetValue("ReasonCode", out var actualReason)
                        && string.Equals(actualReason?.ToString(), reasonCode, StringComparison.Ordinal)
                        && entry.State.TryGetValue("MediaType", out var actualMediaType)
                        && string.Equals(actualMediaType?.ToString(), mediaType, StringComparison.Ordinal)),
                    $"Expected applied reason {reasonCode} for {mediaType} on TmdbCorrection.Applied.");
            }
        }

        private sealed class RecordingLoggerProvider : ILoggerProvider
        {
            public List<LogEntry> Entries { get; } = new List<LogEntry>();

            public ILogger CreateLogger(string categoryName)
            {
                return new RecordingLogger(categoryName, this.Entries);
            }

            public void Dispose()
            {
            }
        }

        private sealed class RecordingLogger : ILogger
        {
            private readonly string categoryName;
            private readonly List<LogEntry> entries;

            public RecordingLogger(string categoryName, List<LogEntry> entries)
            {
                this.categoryName = categoryName;
                this.entries = entries;
            }

            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull
            {
                _ = state;
                return NullScope.Instance;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                _ = logLevel;
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var structuredState = ToStructuredState(state);
                this.entries.Add(new LogEntry(this.categoryName, logLevel, eventId, structuredState, formatter(state, exception)));
            }

            private static IReadOnlyDictionary<string, object?> ToStructuredState<TState>(TState state)
            {
                if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    return pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                }

                return new Dictionary<string, object?>(StringComparer.Ordinal);
            }
        }

        private sealed record LogEntry(string CategoryName, LogLevel Level, EventId EventId, IReadOnlyDictionary<string, object?> State, string Message);

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new NullScope();

            private NullScope()
            {
            }

            public void Dispose()
            {
            }
        }
    }
}
