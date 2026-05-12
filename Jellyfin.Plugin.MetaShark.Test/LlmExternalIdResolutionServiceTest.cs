using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.MetaShark;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Providers.Llm;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using TMDbLib.Objects.Find;
using TMDbLib.Objects.Search;
using TmdbMovie = TMDbLib.Objects.Movies.Movie;
using TmdbTvShow = TMDbLib.Objects.TvShows.TvShow;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [TestCategory("Stable")]
    [DoNotParallelize]
    public class LlmExternalIdResolutionServiceTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-llm-external-id-resolution-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");

        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder => { });

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
        public async Task ResolveAsync_WhenTmdbMovieVerified_ShouldWriteMissingProviderIdOnly()
        {
            var tmdbApi = this.CreateTmdbApi();
            SeedTmdbMovie(tmdbApi, 27205, "zh-CN", string.Empty);
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "27205", "Movie")));
            var service = this.CreateService(llmApi, tmdbApi);
            var lookupInfo = new MovieInfo { Name = "Inception", Year = 2010, MetadataLanguage = "zh-CN", ProviderIds = new Dictionary<string, string>() };

            var result = await service.ResolveAsync(CreateRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.Succeeded, result.Status, result.Diagnostic);
            Assert.AreEqual("27205", lookupInfo.ProviderIds[MetadataProvider.Tmdb.ToString()]);
            Assert.AreEqual(1, result.ProviderIdWrites.Count);
            Assert.AreEqual(MetadataProvider.Tmdb.ToString(), result.ProviderIdWrites[0].ProviderIdKey);
            Assert.AreEqual(1, llmApi.Prompts.Count);
            AssertPromptUsesExternalIdSchema(llmApi.Prompts[0]);
        }

        [TestMethod]
        public async Task ResolveAsync_WhenProviderIdExistsAndCorrectionSwitchOff_ShouldNotOverwriteThroughOrdinaryResolver()
        {
            var tmdbApi = this.CreateTmdbApi();
            SeedTmdbMovie(tmdbApi, 27205, "zh-CN", string.Empty);
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "27205", "Movie")));
            var service = this.CreateService(llmApi, tmdbApi);
            var lookupInfo = new MovieInfo
            {
                Name = "Inception",
                Year = 2010,
                MetadataLanguage = "zh-CN",
                ProviderIds = new Dictionary<string, string> { [MetadataProvider.Tmdb.ToString()] = "existing" },
            };

            var request = CreateRequest(lookupInfo);
            request.Configuration!.EnableLlmTmdbIdCorrection = false;

            var result = await service.ResolveAsync(request, CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(request.Configuration.EnableLlmTmdbIdCorrection);
            Assert.AreEqual(LlmExternalIdResolutionStatus.Skipped, result.Status, result.Diagnostic);
            Assert.AreEqual("existing", lookupInfo.ProviderIds[MetadataProvider.Tmdb.ToString()]);
            Assert.AreEqual(0, result.ProviderIdWrites.Count);
            Assert.AreEqual(1, result.SkippedProviderIdWrites.Count);
            Assert.IsTrue(result.Diagnostic.Contains("not overwritten", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public async Task ResolveAsync_WhenTextCompletionDisabled_ShouldStillResolveExternalId()
        {
            var tmdbApi = this.CreateTmdbApi();
            SeedTmdbMovie(tmdbApi, 27205, "zh-CN", string.Empty);
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "27205", "Movie")));
            var service = this.CreateService(llmApi, tmdbApi);
            var lookupInfo = new MovieInfo { Name = "Inception", Year = 2010, MetadataLanguage = "zh-CN", ProviderIds = new Dictionary<string, string>() };
            var request = CreateRequest(lookupInfo);
            request.Configuration!.LlmAllowTextCompletion = false;

            var result = await service.ResolveAsync(request, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.Succeeded, result.Status, result.Diagnostic);
            Assert.AreEqual("27205", lookupInfo.ProviderIds[MetadataProvider.Tmdb.ToString()]);
            Assert.AreEqual(1, llmApi.Prompts.Count);
        }

        [TestMethod]
        public async Task ResolveAsync_WhenRelativePathContextDisabled_ShouldNotIncludePathsInPrompt()
        {
            var llmApi = new RecordingLlmApi(@"{ ""externalIdCandidates"": [] }");
            var service = this.CreateService(llmApi, this.CreateTmdbApi());
            var lookupInfo = new EpisodeInfo
            {
                Name = "Episode 1",
                Path = "/mnt/media/Shows/Series A/Season 01/S01E01.mkv",
                MetadataLanguage = "zh-CN",
                ParentIndexNumber = 1,
                IndexNumber = 1,
                ProviderIds = new Dictionary<string, string>(),
                SeriesProviderIds = new Dictionary<string, string> { [MetadataProvider.Tmdb.ToString()] = "1396" },
            };
            var request = CreateRequest(lookupInfo, mediaType: "Episode");
            request.Configuration!.LlmAllowRelativePathContext = false;
            request.RelativePathSamples = new[] { "/mnt/media/Shows/Series A/Season 01/S01E02.mkv", "Shows/Series A/Season 01/S01E03.mkv" };

            var result = await service.ResolveAsync(request, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.Skipped, result.Status, result.Diagnostic);
            Assert.AreEqual(1, llmApi.Prompts.Count);
            var prompt = llmApi.Prompts.Single();
            Assert.IsFalse(prompt.Contains("/mnt/media", StringComparison.OrdinalIgnoreCase), prompt);
            Assert.IsFalse(prompt.Contains("Shows/Series A", StringComparison.Ordinal), prompt);
            Assert.IsFalse(prompt.Contains("S01E", StringComparison.OrdinalIgnoreCase), prompt);
            using var document = JsonDocument.Parse(prompt);
            Assert.AreEqual(0, document.RootElement.GetProperty("SafeRelativePathSamples").GetArrayLength());
        }

        [TestMethod]
        public async Task ResolveAsync_WhenLimiterBusy_ShouldSkipWithoutCallingApi()
        {
            using var limiter = new LlmRequestLimiter();
            using var heldLease = await limiter.TryAcquireAsync(CancellationToken.None).ConfigureAwait(false);
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "27205", "Movie")));
            var service = this.CreateService(llmApi, this.CreateTmdbApi(), requestLimiter: limiter);
            var lookupInfo = new MovieInfo { Name = "Inception", Year = 2010, MetadataLanguage = "zh-CN", ProviderIds = new Dictionary<string, string>() };

            var result = await service.ResolveAsync(CreateRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.Skipped, result.Status);
            Assert.AreEqual("LlmRequestLimiterBusy", result.Diagnostic);
            Assert.AreEqual(0, llmApi.Prompts.Count);
            Assert.AreEqual(0, lookupInfo.ProviderIds.Count);
        }

        [TestMethod]
        public async Task ResolveAsync_WhenLlmJsonInvalid_ShouldReturnValidationFailureWithoutWrite()
        {
            var llmApi = new RecordingLlmApi(@"{ ""candidates"": [] }");
            var service = this.CreateService(llmApi, this.CreateTmdbApi());
            var lookupInfo = new MovieInfo { Name = "Inception", MetadataLanguage = "zh-CN", ProviderIds = new Dictionary<string, string>() };

            var result = await service.ResolveAsync(CreateRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.ValidationFailed, result.Status);
            Assert.AreEqual(0, lookupInfo.ProviderIds.Count);
        }

        [TestMethod]
        public async Task ResolveAsync_WhenLlmReturnsEmptyCanonicalCandidateList_ShouldSkipWithoutWrite()
        {
            var llmApi = new RecordingLlmApi(@"{ ""externalIdCandidates"": [] }");
            var service = this.CreateService(llmApi, this.CreateTmdbApi());
            var lookupInfo = new MovieInfo { Name = "Inception", MetadataLanguage = "zh-CN", ProviderIds = new Dictionary<string, string>() };

            var result = await service.ResolveAsync(CreateRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.Skipped, result.Status, result.Diagnostic);
            Assert.AreEqual(0, lookupInfo.ProviderIds.Count);
            Assert.AreEqual(0, result.ProviderIdWrites.Count);
            Assert.AreEqual(0, result.VerifiedCandidates.Count);
            Assert.IsTrue(result.Diagnostic.Contains("no candidates", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public async Task ResolveAsync_WhenLlmJsonMalformed_ShouldReturnValidationFailureWithoutWrite()
        {
            var llmApi = new RecordingLlmApi(@"{ ""externalIdCandidates"": [");
            var service = this.CreateService(llmApi, this.CreateTmdbApi());
            var lookupInfo = new MovieInfo { Name = "Inception", MetadataLanguage = "zh-CN", ProviderIds = new Dictionary<string, string>() };

            var result = await service.ResolveAsync(CreateRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.ValidationFailed, result.Status);
            Assert.AreEqual(0, lookupInfo.ProviderIds.Count);
            Assert.IsTrue(result.Diagnostic.Contains("schema invalid", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public async Task ResolveAsync_WhenCandidateIdFormatIllegal_ShouldReturnValidationFailureWithoutWrite()
        {
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "tmdb-27205", "Movie")));
            var service = this.CreateService(llmApi, this.CreateTmdbApi());
            var lookupInfo = new MovieInfo { Name = "Inception", MetadataLanguage = "zh-CN", ProviderIds = new Dictionary<string, string>() };

            var result = await service.ResolveAsync(CreateRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.ValidationFailed, result.Status);
            Assert.AreEqual(0, lookupInfo.ProviderIds.Count);
            Assert.IsTrue(result.Diagnostic.Contains("id format", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public async Task ResolveAsync_WhenCandidateConfidenceBelowThreshold_ShouldReturnValidationFailureWithoutWrite()
        {
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "27205", "Movie", confidence: 0.5)));
            var service = this.CreateService(llmApi, this.CreateTmdbApi());
            var lookupInfo = new MovieInfo { Name = "Inception", MetadataLanguage = "zh-CN", ProviderIds = new Dictionary<string, string>() };

            var result = await service.ResolveAsync(CreateRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.ValidationFailed, result.Status);
            Assert.AreEqual(0, lookupInfo.ProviderIds.Count);
            Assert.IsTrue(result.Diagnostic.Contains("below threshold", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public async Task ResolveAsync_WhenCandidateContainsProviderIdsField_ShouldReturnValidationFailureWithoutWrite()
        {
            var llmApi = new RecordingLlmApi("{\"externalIdCandidates\":[{\"provider\":\"TMDb\",\"id\":\"27205\",\"mediaType\":\"Movie\",\"confidence\":0.9,\"reason\":\"title and year match\",\"evidence\":\"filename match\",\"ProviderIds\":{\"TMDb\":\"27205\"}}]}");
            var service = this.CreateService(llmApi, this.CreateTmdbApi());
            var lookupInfo = new MovieInfo { Name = "Inception", MetadataLanguage = "zh-CN", ProviderIds = new Dictionary<string, string>() };

            var result = await service.ResolveAsync(CreateRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.ValidationFailed, result.Status);
            Assert.AreEqual(0, lookupInfo.ProviderIds.Count);
            Assert.IsTrue(result.Diagnostic.Contains("ProviderIds", StringComparison.Ordinal), result.Diagnostic);
        }

        [TestMethod]
        public async Task ResolveAsync_WhenCandidateContainsLowercaseProviderIdsField_ShouldReturnValidationFailureWithoutWrite()
        {
            var llmApi = new RecordingLlmApi("{\"externalIdCandidates\":[{\"provider\":\"TMDb\",\"id\":\"27205\",\"mediaType\":\"Movie\",\"confidence\":0.9,\"reason\":\"title and year match\",\"evidence\":\"filename match\",\"providerIds\":{\"TMDb\":\"27205\"}}]}");
            var service = this.CreateService(llmApi, this.CreateTmdbApi());
            var lookupInfo = new MovieInfo { Name = "Inception", MetadataLanguage = "zh-CN", ProviderIds = new Dictionary<string, string>() };

            var result = await service.ResolveAsync(CreateRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.ValidationFailed, result.Status);
            Assert.AreEqual(0, lookupInfo.ProviderIds.Count);
            Assert.IsTrue(result.Diagnostic.Contains("providerIds", StringComparison.Ordinal), result.Diagnostic);
        }

        [DataTestMethod]
        [DataRow("ProviderIds")]
        [DataRow("providerIds")]
        public async Task ResolveAsync_WhenResponseContainsTopLevelProviderIdsField_ShouldReturnValidationFailureWithoutWrite(string fieldName)
        {
            var llmApi = new RecordingLlmApi("{\"externalIdCandidates\":[" + CandidateJson("TMDb", "27205", "Movie") + "],\"" + fieldName + "\":{\"TMDb\":\"27205\"}}");
            var service = this.CreateService(llmApi, this.CreateTmdbApi());
            var lookupInfo = new MovieInfo { Name = "Inception", MetadataLanguage = "zh-CN", ProviderIds = new Dictionary<string, string>() };

            var result = await service.ResolveAsync(CreateRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.ValidationFailed, result.Status);
            Assert.AreEqual(0, lookupInfo.ProviderIds.Count);
            Assert.IsTrue(result.Diagnostic.Contains(fieldName, StringComparison.Ordinal), result.Diagnostic);
        }

        [TestMethod]
        public async Task ResolveAsync_WhenTmdbVerificationFails_ShouldPreserveProviderIds()
        {
            var tmdbApi = this.CreateTmdbApi();
            SeedMissingTmdbMovie(tmdbApi, 27205, "zh-CN", string.Empty);
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "27205", "Movie")));
            var service = this.CreateService(llmApi, tmdbApi);
            var lookupInfo = new MovieInfo { Name = "Inception", MetadataLanguage = "zh-CN", ProviderIds = new Dictionary<string, string>() };

            var result = await service.ResolveAsync(CreateRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.VerificationFailed, result.Status, result.Diagnostic);
            Assert.AreEqual(0, lookupInfo.ProviderIds.Count);
            Assert.IsTrue(result.Diagnostic.Contains("TMDb movie", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public async Task ResolveAsync_WhenCandidateMediaTypeMismatchesTarget_ShouldRejectWithoutWrite()
        {
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "1396", "Series")));
            var service = this.CreateService(llmApi, this.CreateTmdbApi());
            var lookupInfo = new MovieInfo { Name = "Breaking Bad", MetadataLanguage = "zh-CN", ProviderIds = new Dictionary<string, string>() };

            var result = await service.ResolveAsync(CreateRequest(lookupInfo, mediaType: "Movie"), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.VerificationFailed, result.Status, result.Diagnostic);
            Assert.AreEqual(0, lookupInfo.ProviderIds.Count);
            Assert.IsTrue(result.Diagnostic.Contains("media type", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public async Task ResolveAsync_WhenVerifiedCandidatesAreAmbiguous_ShouldRejectAndPreserveProviderIds()
        {
            var tmdbApi = this.CreateTmdbApi();
            SeedTmdbMovie(tmdbApi, 27205, "zh-CN", string.Empty);
            SeedTmdbMovie(tmdbApi, 27206, "zh-CN", string.Empty);
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "27205", "Movie"), CandidateJson("TMDb", "27206", "Movie")));
            var service = this.CreateService(llmApi, tmdbApi);
            var lookupInfo = new MovieInfo { Name = "Inception", MetadataLanguage = "zh-CN", ProviderIds = new Dictionary<string, string>() };

            var result = await service.ResolveAsync(CreateRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.Rejected, result.Status, result.Diagnostic);
            Assert.AreEqual(0, lookupInfo.ProviderIds.Count);
            Assert.IsTrue(result.Diagnostic.Contains("AmbiguousCandidates", StringComparison.Ordinal), result.Diagnostic);
        }

        [TestMethod]
        public async Task ResolveAsync_WhenVerificationThrows_ShouldFailClosedWithoutWrite()
        {
            var tmdbApi = this.CreateTmdbApi();
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "27205", "Movie")));
            var service = this.CreateService(llmApi, tmdbApi);
            tmdbApi.Dispose();
            var lookupInfo = new MovieInfo { Name = "Inception", MetadataLanguage = "zh-CN", ProviderIds = new Dictionary<string, string>() };

            var result = await service.ResolveAsync(CreateRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.VerificationFailed, result.Status, result.Diagnostic);
            Assert.AreEqual(0, lookupInfo.ProviderIds.Count);
            Assert.IsTrue(result.Diagnostic.Contains("verification failed", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
            Assert.IsTrue(result.Diagnostic.Contains("ObjectDisposedException", StringComparison.Ordinal), result.Diagnostic);
        }

        [TestMethod]
        public async Task ResolveAsync_WhenAutomaticRefresh_ShouldNotCallLlmOrWrite()
        {
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "27205", "Movie")));
            var service = this.CreateService(llmApi, this.CreateTmdbApi());
            var lookupInfo = new MovieInfo { Name = "Inception", MetadataLanguage = "zh-CN", ProviderIds = new Dictionary<string, string>() };
            var request = CreateRequest(lookupInfo);
            request.Semantic = DefaultScraperSemantic.AutomaticRefresh;
            request.HttpContext = null;

            var result = await service.ResolveAsync(request, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.NotTriggered, result.Status);
            Assert.AreEqual(0, llmApi.Prompts.Count);
            Assert.AreEqual(0, lookupInfo.ProviderIds.Count);
        }

        [TestMethod]
        public async Task ResolveAsync_WhenUserRefreshWithoutHttpContext_ShouldNotCallLlmOrWrite()
        {
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "27205", "Movie")));
            var service = this.CreateService(llmApi, this.CreateTmdbApi());
            var lookupInfo = new MovieInfo { Name = "Inception", MetadataLanguage = "zh-CN", ProviderIds = new Dictionary<string, string>() };
            var request = CreateRequest(lookupInfo);
            request.Semantic = DefaultScraperSemantic.UserRefresh;
            request.HttpContext = null;

            var result = await service.ResolveAsync(request, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.NotTriggered, result.Status);
            Assert.AreEqual(0, llmApi.Prompts.Count);
            Assert.AreEqual(0, lookupInfo.ProviderIds.Count);
            Assert.IsTrue(result.Diagnostic.Contains("ImplicitRefreshRejected", StringComparison.Ordinal), result.Diagnostic);
        }

        [TestMethod]
        public async Task ResolveAsync_WhenSeasonCandidateVerified_ShouldReturnParentIdWithoutWritingSeasonProviderId()
        {
            var tmdbApi = this.CreateTmdbApi();
            SeedTmdbSeries(tmdbApi, 1396, "zh-CN", string.Empty);
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "1396", "Series")));
            var service = this.CreateService(llmApi, tmdbApi);
            var lookupInfo = new SeasonInfo { Name = "Season 1", MetadataLanguage = "zh-CN", ProviderIds = new Dictionary<string, string>() };

            var result = await service.ResolveAsync(CreateRequest(lookupInfo, mediaType: "Season"), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.Skipped, result.Status, result.Diagnostic);
            Assert.AreEqual(0, lookupInfo.ProviderIds.Count);
            Assert.AreEqual(1, result.VerifiedCandidates.Count);
            Assert.AreEqual("Series", result.VerifiedCandidates[0].MediaType);
            Assert.AreEqual("1396", result.VerifiedCandidates[0].Id);
        }

        [TestMethod]
        public async Task ResolveAsync_WhenEpisodeTargetHasTmdbSeriesCandidate_ShouldReturnParentCandidateWithoutWritingEpisodeProviderId()
        {
            var tmdbApi = this.CreateTmdbApi();
            SeedTmdbSeries(tmdbApi, 1396, "zh-CN", string.Empty);
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "1396", "Series")));
            var service = this.CreateService(llmApi, tmdbApi);
            var lookupInfo = new EpisodeInfo
            {
                Name = "Episode 1",
                MetadataLanguage = "zh-CN",
                ParentIndexNumber = 1,
                IndexNumber = 1,
                ProviderIds = new Dictionary<string, string>(),
                SeriesProviderIds = new Dictionary<string, string>(),
            };

            var result = await service.ResolveAsync(CreateRequest(lookupInfo, mediaType: "Episode"), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.Skipped, result.Status, result.Diagnostic);
            Assert.AreEqual(0, lookupInfo.ProviderIds.Count);
            Assert.AreEqual(0, result.ProviderIdWrites.Count);
            Assert.AreEqual(1, result.VerifiedCandidates.Count);
            Assert.AreEqual("TMDb", result.VerifiedCandidates[0].Provider);
            Assert.AreEqual("Series", result.VerifiedCandidates[0].MediaType);
            Assert.AreEqual("1396", result.VerifiedCandidates[0].Id);
        }

        [TestMethod]
        public async Task ResolveAsync_WhenTvdbEpisodeVerifiedByParentSeriesSeasonAndEpisode_ShouldWriteMissingProviderId()
        {
            ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableTvdbSpecialsWithinSeasons = true,
                TvdbApiKey = "test-key",
            });
            var tvdbApi = CreateTvdbApiWithResponses(CreateTvdbEpisodesResponse(7001, 1, 1));
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TVDB", "7001", "Episode")));
            var service = this.CreateService(llmApi, this.CreateTmdbApi(), tvdbApi);
            var lookupInfo = CreateEpisodeLookupInfo(seriesTvdbId: "321", seasonNumber: 1, episodeNumber: 1);

            var result = await service.ResolveAsync(CreateRequest(lookupInfo, mediaType: "Episode"), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.Succeeded, result.Status, result.Diagnostic);
            Assert.AreEqual("7001", lookupInfo.ProviderIds[MetadataProvider.Tvdb.ToString()]);
            Assert.AreEqual(1, result.ProviderIdWrites.Count);
            Assert.AreEqual(MetadataProvider.Tvdb.ToString(), result.ProviderIdWrites[0].ProviderIdKey);
        }

        [TestMethod]
        public async Task ResolveAsync_WhenTvdbEpisodeIdMismatchesVerifiedList_ShouldFailClosedWithoutWrite()
        {
            ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableTvdbSpecialsWithinSeasons = true,
                TvdbApiKey = "test-key",
            });
            var tvdbApi = CreateTvdbApiWithResponses(CreateTvdbEpisodesResponse(7002, 1, 1));
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TVDB", "7001", "Episode")));
            var service = this.CreateService(llmApi, this.CreateTmdbApi(), tvdbApi);
            var lookupInfo = CreateEpisodeLookupInfo(seriesTvdbId: "321", seasonNumber: 1, episodeNumber: 1);

            var result = await service.ResolveAsync(CreateRequest(lookupInfo, mediaType: "Episode"), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.VerificationFailed, result.Status, result.Diagnostic);
            Assert.AreEqual(0, lookupInfo.ProviderIds.Count);
            Assert.IsTrue(result.Diagnostic.Contains("TVDB episode detail did not match", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [DataTestMethod]
        [DataRow(7001, 2, 1, DisplayName = "season mismatch")]
        [DataRow(7001, 1, 2, DisplayName = "episode mismatch")]
        public async Task ResolveAsync_WhenTvdbEpisodeListDoesNotMatchCurrentSeasonEpisode_ShouldFailClosedWithoutWrite(int responseEpisodeId, int responseSeasonNumber, int responseEpisodeNumber)
        {
            ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableTvdbSpecialsWithinSeasons = true,
                TvdbApiKey = "test-key",
            });
            var tvdbApi = CreateTvdbApiWithResponses(CreateTvdbEpisodesResponse(responseEpisodeId, responseSeasonNumber, responseEpisodeNumber));
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TVDB", "7001", "Episode")));
            var service = this.CreateService(llmApi, this.CreateTmdbApi(), tvdbApi);
            var lookupInfo = CreateEpisodeLookupInfo(seriesTvdbId: "321", seasonNumber: 1, episodeNumber: 1);

            var result = await service.ResolveAsync(CreateRequest(lookupInfo, mediaType: "Episode"), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.VerificationFailed, result.Status, result.Diagnostic);
            Assert.AreEqual(0, lookupInfo.ProviderIds.Count);
            Assert.IsTrue(result.Diagnostic.Contains("TVDB episode detail did not match", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public async Task ResolveAsync_WhenTvdbEpisodeListIsEmpty_ShouldFailClosedWithoutWrite()
        {
            ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableTvdbSpecialsWithinSeasons = true,
                TvdbApiKey = "test-key",
            });
            var tvdbApi = CreateTvdbApiWithResponses(CreateTvdbEpisodesResponse());
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TVDB", "7001", "Episode")));
            var service = this.CreateService(llmApi, this.CreateTmdbApi(), tvdbApi);
            var lookupInfo = CreateEpisodeLookupInfo(seriesTvdbId: "321", seasonNumber: 1, episodeNumber: 1);

            var result = await service.ResolveAsync(CreateRequest(lookupInfo, mediaType: "Episode"), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.VerificationFailed, result.Status, result.Diagnostic);
            Assert.AreEqual(0, lookupInfo.ProviderIds.Count);
        }

        [TestMethod]
        public async Task ResolveAsync_WhenTvdbApiReturnsFailure_ShouldFailClosedWithoutWrite()
        {
            ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableTvdbSpecialsWithinSeasons = true,
                TvdbApiKey = "test-key",
            });
            var tvdbApi = CreateTvdbApiWithResponses(CreateTvdbEpisodesResponse(), HttpStatusCode.BadGateway);
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TVDB", "7001", "Episode")));
            var service = this.CreateService(llmApi, this.CreateTmdbApi(), tvdbApi);
            var lookupInfo = CreateEpisodeLookupInfo(seriesTvdbId: "321", seasonNumber: 1, episodeNumber: 1);

            var result = await service.ResolveAsync(CreateRequest(lookupInfo, mediaType: "Episode"), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.VerificationFailed, result.Status, result.Diagnostic);
            Assert.AreEqual(0, lookupInfo.ProviderIds.Count);
        }

        [TestMethod]
        public async Task ResolveAsync_WhenTvdbEpisodeParentSeriesIsMissing_ShouldFailClosedWithoutWrite()
        {
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TVDB", "7001", "Episode")));
            var service = this.CreateService(llmApi, this.CreateTmdbApi(), this.CreateTvdbApi());
            var lookupInfo = CreateEpisodeLookupInfo(seriesTvdbId: null, seasonNumber: 1, episodeNumber: 1);

            var result = await service.ResolveAsync(CreateRequest(lookupInfo, mediaType: "Episode"), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.VerificationFailed, result.Status, result.Diagnostic);
            Assert.AreEqual(0, lookupInfo.ProviderIds.Count);
            Assert.IsTrue(result.Diagnostic.Contains("parent TVDB series", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public async Task ResolveAsync_WhenTvdbSeriesCandidateTargetsSeries_ShouldRemainRejectedWithoutWrite()
        {
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TVDB", "321", "Series")));
            var service = this.CreateService(llmApi, this.CreateTmdbApi(), this.CreateTvdbApi());
            var lookupInfo = new SeriesInfo { Name = "Series A", MetadataLanguage = "zh-CN", ProviderIds = new Dictionary<string, string>() };

            var result = await service.ResolveAsync(CreateRequest(lookupInfo, mediaType: "Series"), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.VerificationFailed, result.Status, result.Diagnostic);
            Assert.AreEqual(0, lookupInfo.ProviderIds.Count);
            Assert.IsTrue(result.Diagnostic.Contains("TVDB series ownership cannot be verified", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
        }

        [TestMethod]
        public async Task EvaluateExistingProviderIdsAsync_WhenExistingMovieIdsAreSemanticallyConsistent_ShouldShortCircuitWithoutBackendCall()
        {
            var tmdbApi = this.CreateTmdbApi();
            SeedTmdbMovie(tmdbApi, 27205, "zh-CN", string.Empty);
            SeedFindByExternalId(tmdbApi, FindExternalSource.Imdb, "tt1375666", "zh-CN", movieIds: new[] { 27205 });
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "99999", "Movie")));
            var service = this.CreateService(llmApi, tmdbApi);
            var lookupInfo = new MovieInfo
            {
                Name = "Inception",
                Year = 2010,
                Path = "/mnt/media/Movies/Inception (2010)/Inception.mkv",
                MetadataLanguage = "zh-CN",
                ProviderIds = new Dictionary<string, string>
                {
                    [MetadataProvider.Tmdb.ToString()] = "27205",
                    [MetadataProvider.Imdb.ToString()] = "tt1375666",
                },
            };

            var decision = await service.EvaluateExistingProviderIdsAsync(CreateRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(decision.ShouldTrigger, decision.Reason);
            Assert.AreEqual("ExistingProviderIdsConsistent", decision.Reason);
            Assert.AreEqual(0, llmApi.Prompts.Count);
        }

        [TestMethod]
        public async Task EvaluateExistingProviderIdsAsync_WhenExistingMovieIdsUseLocalMetadataOnly_ShouldRemainConsistentUntilVerifiedEvidenceSaysOtherwise()
        {
            var tmdbApi = this.CreateTmdbApi();
            SeedTmdbMovie(tmdbApi, 111, "zh-CN", string.Empty);
            SeedFindByExternalId(tmdbApi, FindExternalSource.Imdb, "tt1375666", "zh-CN", movieIds: new[] { 111 });
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "27205", "Movie")));
            var service = this.CreateService(llmApi, tmdbApi);
            var lookupInfo = new MovieInfo
            {
                Name = "Resolved Movie",
                Year = 2020,
                Path = "/mnt/media/Movies/Resolved Movie (2020)/Resolved Movie.mkv",
                MetadataLanguage = "zh-CN",
                ProviderIds = new Dictionary<string, string>
                {
                    [MetadataProvider.Tmdb.ToString()] = "111",
                    [MetadataProvider.Imdb.ToString()] = "tt1375666",
                },
            };

            var decision = await service.EvaluateExistingProviderIdsAsync(CreateRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(decision.ShouldTrigger, decision.Reason);
            Assert.AreEqual("ExistingProviderIdsConsistent", decision.Reason);
            Assert.AreEqual(0, llmApi.Prompts.Count);
        }

        [TestMethod]
        public async Task ResolveAsync_WhenValidationFailsWithExistingNonTmdbIds_ShouldPreserveOriginalIdsFailClosed()
        {
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "27205", "Movie", confidence: 0.5)));
            var service = this.CreateService(llmApi, this.CreateTmdbApi());
            var lookupInfo = new MovieInfo
            {
                Name = "Inception",
                Year = 2010,
                Path = "/mnt/media/Movies/Inception (2010)/Inception.mkv",
                MetadataLanguage = "zh-CN",
                ProviderIds = new Dictionary<string, string>
                {
                    [MetadataProvider.Imdb.ToString()] = "tt1375666",
                    [MetadataProvider.Tvdb.ToString()] = "81189",
                    [BaseProvider.DoubanProviderId] = "1291843",
                },
            };
            var providerIdsSnapshot = new Dictionary<string, string>(lookupInfo.ProviderIds, StringComparer.OrdinalIgnoreCase);

            var result = await service.ResolveAsync(CreateRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.ValidationFailed, result.Status);
            CollectionAssert.AreEquivalent(providerIdsSnapshot.ToArray(), lookupInfo.ProviderIds.ToArray());
            Assert.AreEqual("tt1375666", lookupInfo.ProviderIds[MetadataProvider.Imdb.ToString()]);
            Assert.AreEqual("81189", lookupInfo.ProviderIds[MetadataProvider.Tvdb.ToString()]);
            Assert.AreEqual("1291843", lookupInfo.ProviderIds[BaseProvider.DoubanProviderId]);
        }

        [TestMethod]
        public async Task EvaluateExistingProviderIdsAsync_WhenSemanticConflictFixtureContainsMultipleStaleIds_ShouldReturnStaleExternalIdConflict()
        {
            var tmdbApi = this.CreateTmdbApi();
            GetTmdbMemoryCache(tmdbApi).Set(
                "series-111-zh-CN-",
                new TmdbTvShow { Id = 111, Name = "短篇特典", OriginalName = "短篇特典", FirstAirDate = new DateTime(1999, 1, 1) },
                TimeSpan.FromMinutes(5));
            SeedFindByExternalId(tmdbApi, FindExternalSource.Imdb, "ttspin111", "zh-CN", seriesIds: new[] { 111 });
            SeedFindByExternalId(tmdbApi, FindExternalSource.TvDb, "tvdb-spin-111", "zh-CN", seriesIds: new[] { 111 });
            var doubanApi = this.CreateDoubanApi();
            SeedDoubanSubject(doubanApi, "douban-spin-111", new DoubanSubject { Sid = "douban-spin-111", Category = "电视剧", Name = "短篇特典", OriginalName = "短篇特典", Year = 1999 });
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "222", "Series", reason: "semantic conflict", evidence: "relative path aligns with main series")));
            var service = this.CreateService(llmApi, tmdbApi, doubanApi: doubanApi);
            var lookupInfo = new SeriesInfo
            {
                Name = "主线剧集",
                Year = 2024,
                Path = "/mnt/media/Shows/主线剧集 (2024)/短篇特典/第01集.mkv",
                MetadataLanguage = "zh-CN",
                ProviderIds = new Dictionary<string, string>
                {
                    [MetadataProvider.Tmdb.ToString()] = "111",
                    [MetadataProvider.Imdb.ToString()] = "ttspin111",
                    [MetadataProvider.Tvdb.ToString()] = "tvdb-spin-111",
                    [BaseProvider.DoubanProviderId] = "douban-spin-111",
                },
            };

            var request = CreateRequest(lookupInfo, mediaType: "Series");
            request.RelativePathSamples = new[]
            {
                "Shows/主线剧集 (2024)/Season 01/第01集.mkv",
                "Shows/主线剧集 (2024)/Season 01/第02集.mkv",
            };

            var decision = await service.EvaluateExistingProviderIdsAsync(request, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(decision.ShouldTrigger, decision.Reason);
            Assert.AreEqual("StaleExternalIdConflict", decision.Reason);
            Assert.AreEqual(0, llmApi.Prompts.Count, "发现已有公开 ID 证据冲突后，应直接进入 correction 评估，不需要普通 resolver prompt。");
        }

        [TestMethod]
        public async Task TryResolveTmdbCorrectionAsync_WhenImdbUniqueMappingProvesOldWrong_ShouldReturnReplacementOnly()
        {
            var tmdbApi = this.CreateTmdbApi();
            SeedTmdbMovie(tmdbApi, 222, "zh-CN", string.Empty);
            SeedFindByExternalId(tmdbApi, FindExternalSource.Imdb, "tt1234567", "zh-CN", movieIds: new[] { 222 });
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "222", "Movie", reason: "public id conflict", evidence: "IMDb evidence")));
            var service = this.CreateService(llmApi, tmdbApi);
            var lookupInfo = CreateMovieCorrectionLookupInfo("111", imdbId: "tt1234567");

            var result = await service.TryResolveTmdbCorrectionAsync(CreateCorrectionRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.ShouldReplace, result.Diagnostic);
            Assert.AreEqual("222", result.ReplacementTmdbId);
            Assert.AreEqual("111", lookupInfo.ProviderIds[MetadataProvider.Tmdb.ToString()]);
            Assert.AreEqual("tt1234567", lookupInfo.ProviderIds[MetadataProvider.Imdb.ToString()]);
            Assert.AreEqual(1, llmApi.Prompts.Count);
        }

        [TestMethod]
        public async Task TryResolveTmdbCorrectionAsync_WhenSemanticConflictVerificationSucceeds_ShouldReturnReplacementAndPreserveNonTmdbIds()
        {
            var tmdbApi = this.CreateTmdbApi();
            GetTmdbMemoryCache(tmdbApi).Set(
                "series-222-zh-CN-",
                new TmdbTvShow { Id = 222, Name = "主线剧集", OriginalName = "主线剧集", FirstAirDate = new DateTime(2024, 1, 1) },
                TimeSpan.FromMinutes(5));
            SeedFindByExternalId(tmdbApi, FindExternalSource.Imdb, "ttmain222", "zh-CN", seriesIds: new[] { 222 });
            SeedFindByExternalId(tmdbApi, FindExternalSource.TvDb, "tvdb-main-222", "zh-CN", seriesIds: new[] { 222 });
            var doubanApi = this.CreateDoubanApi();
            SeedDoubanSubject(doubanApi, "douban-main-222", new DoubanSubject { Sid = "douban-main-222", Category = "电视剧", Name = "主线剧集", OriginalName = "主线剧集", Year = 2024 });
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "222", "Series", reason: "semantic conflict", evidence: "relative path and public ids align with main series")));
            var service = this.CreateService(llmApi, tmdbApi, doubanApi: doubanApi);
            var lookupInfo = CreateSeriesCorrectionLookupInfo("111", imdbId: "ttmain222", tvdbId: "tvdb-main-222", doubanId: "douban-main-222");
            lookupInfo.Name = "主线剧集";
            lookupInfo.Year = 2024;
            lookupInfo.Path = "/mnt/media/Shows/主线剧集 (2024)/短篇特典/第01集.mkv";

            var request = CreateCorrectionRequest(lookupInfo, mediaType: "Series");
            request.RelativePathSamples = new[]
            {
                "Shows/主线剧集 (2024)/Season 01/第01集.mkv",
                "Shows/主线剧集 (2024)/Season 01/第02集.mkv",
            };

            var result = await service.TryResolveTmdbCorrectionAsync(request, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.ShouldReplace, result.Diagnostic);
            Assert.AreEqual("222", result.ReplacementTmdbId);
            Assert.AreEqual("111", lookupInfo.ProviderIds[MetadataProvider.Tmdb.ToString()], "服务层 correction 结果只返回 replacement，不应直接改写旧 TMDb。");
            Assert.AreEqual("ttmain222", lookupInfo.ProviderIds[MetadataProvider.Imdb.ToString()]);
            Assert.AreEqual("tvdb-main-222", lookupInfo.ProviderIds[MetadataProvider.Tvdb.ToString()]);
            Assert.AreEqual("douban-main-222", lookupInfo.ProviderIds[BaseProvider.DoubanProviderId]);
        }

        [TestMethod]
        public async Task TryResolveTmdbCorrectionAsync_WhenSeriesImdbUniqueMappingProvesOldWrong_ShouldReturnReplacementOnly()
        {
            using var loggerFactory = RecordingLoggerFactory.Create();
            var tmdbApi = this.CreateTmdbApi();
            GetTmdbMemoryCache(tmdbApi).Set(
                "series-222-zh-CN-",
                new TmdbTvShow { Id = 222, Name = "主线剧集", OriginalName = "主线剧集", FirstAirDate = new DateTime(2024, 1, 1) },
                TimeSpan.FromMinutes(5));
            SeedFindByExternalId(tmdbApi, FindExternalSource.Imdb, "tt7654321", "zh-CN", seriesIds: new[] { 222 });
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "222", "Series", reason: "public id conflict", evidence: "IMDb evidence")));
            var service = this.CreateService(llmApi, tmdbApi, loggerFactory: loggerFactory);
            var lookupInfo = CreateSeriesCorrectionLookupInfo("111", imdbId: "tt7654321");

            var result = await service.TryResolveTmdbCorrectionAsync(CreateCorrectionRequest(lookupInfo, mediaType: "Series"), CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.ShouldReplace, result.Diagnostic);
            Assert.AreEqual("222", result.ReplacementTmdbId);
            Assert.AreEqual("111", lookupInfo.ProviderIds[MetadataProvider.Tmdb.ToString()]);
            Assert.AreEqual("tt7654321", lookupInfo.ProviderIds[MetadataProvider.Imdb.ToString()]);
            loggerFactory.ExpectAppliedReason("IMDbUniqueMappingVerified", "Series");
        }

        [TestMethod]
        public async Task TryResolveTmdbCorrectionAsync_WhenCandidateMatchesOldTmdbButVerifies_ShouldConfirmExistingTmdb()
        {
            using var loggerFactory = RecordingLoggerFactory.Create();
            var tmdbApi = this.CreateTmdbApi();
            GetTmdbMemoryCache(tmdbApi).Set(
                "series-65942-zh-CN-",
                new TmdbTvShow { Id = 65942, Name = "Re：从零开始的异世界生活", OriginalName = "Re:ZERO -Starting Life in Another World-", FirstAirDate = new DateTime(2016, 4, 4) },
                TimeSpan.FromMinutes(5));
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "65942", "Series", reason: "relative path points to main series", evidence: "existing TMDb is the main series")));
            var service = this.CreateService(llmApi, tmdbApi, loggerFactory: loggerFactory);
            var lookupInfo = CreateSeriesCorrectionLookupInfo("65942", imdbId: "tt5705718", tvdbId: "305089", doubanId: "26862290");
            lookupInfo.Name = "Re：从零开始的休息时间";
            lookupInfo.Year = 2016;
            lookupInfo.Path = "/mnt/media/TV/Re：从零开始的异世界生活 (2016)";

            var result = await service.TryResolveTmdbCorrectionAsync(CreateCorrectionRequest(lookupInfo, mediaType: "Series"), CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.ShouldReplace, result.Diagnostic);
            Assert.AreEqual("65942", result.ReplacementTmdbId);
            Assert.AreEqual("ExistingTmdbVerified", result.Diagnostic);
            loggerFactory.ExpectAppliedReason("ExistingTmdbVerified", "Series");
        }

        [TestMethod]
        public async Task TryResolveTmdbCorrectionAsync_WhenCandidateVerificationThrows_ShouldReturnNoReplacement()
        {
            using var loggerFactory = RecordingLoggerFactory.Create();
            var tmdbApi = this.CreateTmdbApi();
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "222", "Movie")));
            var service = this.CreateService(llmApi, tmdbApi, loggerFactory: loggerFactory);
            var lookupInfo = CreateMovieCorrectionLookupInfo("111", imdbId: "tt1234567");
            tmdbApi.Dispose();

            var result = await service.TryResolveTmdbCorrectionAsync(CreateCorrectionRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.ShouldReplace, result.Diagnostic);
            Assert.IsTrue(result.Diagnostic.Contains("candidate verification failed", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
            Assert.IsTrue(result.Diagnostic.Contains("ObjectDisposedException", StringComparison.Ordinal), result.Diagnostic);
            Assert.IsFalse(result.Diagnostic.Contains("/mnt/media", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
            Assert.IsFalse(result.Diagnostic.Contains("http://", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
            Assert.AreEqual("111", lookupInfo.ProviderIds[MetadataProvider.Tmdb.ToString()]);
            loggerFactory.ExpectRejectedReason("CandidateVerificationFailed", "Movie", "TmdbCorrection.Rejected");
        }

        [TestMethod]
        public async Task TryResolveTmdbCorrectionAsync_WhenEvidenceVerificationThrows_ShouldReturnNoReplacement()
        {
            using var loggerFactory = RecordingLoggerFactory.Create();
            var tmdbApi = this.CreateTmdbApi();
            SeedTmdbMovie(tmdbApi, 222, "zh-CN", string.Empty);
            var doubanApi = this.CreateDoubanApi();
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "222", "Movie")));
            var service = this.CreateService(llmApi, tmdbApi, doubanApi: doubanApi, loggerFactory: loggerFactory);
            var lookupInfo = CreateMovieCorrectionLookupInfo("111", doubanId: "1290000");
            doubanApi.Dispose();

            var result = await service.TryResolveTmdbCorrectionAsync(CreateCorrectionRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.ShouldReplace, result.Diagnostic);
            Assert.IsTrue(result.Diagnostic.Contains("evidence verification failed", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
            Assert.IsTrue(result.Diagnostic.Contains("ObjectDisposedException", StringComparison.Ordinal), result.Diagnostic);
            Assert.IsFalse(result.Diagnostic.Contains("/mnt/media", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
            Assert.IsFalse(result.Diagnostic.Contains("http://", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
            Assert.AreEqual("111", lookupInfo.ProviderIds[MetadataProvider.Tmdb.ToString()]);
            loggerFactory.ExpectRejectedReason("EvidenceVerificationFailed", "Movie", "TmdbCorrection.Rejected");
        }

        [TestMethod]
        public async Task TryResolveTmdbCorrectionAsync_WhenSemanticConflictVerificationFails_ShouldKeepOriginalIds()
        {
            var tmdbApi = this.CreateTmdbApi();
            SeedTmdbSeries(tmdbApi, 222, "zh-CN", string.Empty);
            SeedFindByExternalId(tmdbApi, FindExternalSource.Imdb, "ttspin111", "zh-CN", seriesIds: new[] { 111 });
            SeedFindByExternalId(tmdbApi, FindExternalSource.TvDb, "tvdb-spin-111", "zh-CN", seriesIds: new[] { 111 });
            var doubanApi = this.CreateDoubanApi();
            SeedDoubanSubject(doubanApi, "douban-spin-111", new DoubanSubject { Sid = "douban-spin-111", Category = "电视剧", Name = "短篇特典", OriginalName = "短篇特典", Year = 1999 });
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "222", "Series", reason: "semantic conflict", evidence: "relative path alone is insufficient")));
            var service = this.CreateService(llmApi, tmdbApi, doubanApi: doubanApi);
            var lookupInfo = CreateSeriesCorrectionLookupInfo("111", imdbId: "ttspin111", tvdbId: "tvdb-spin-111", doubanId: "douban-spin-111");
            lookupInfo.Name = "主线剧集";
            lookupInfo.Year = 2024;
            lookupInfo.Path = "/mnt/media/Shows/主线剧集 (2024)/短篇特典/第01集.mkv";

            var request = CreateCorrectionRequest(lookupInfo, mediaType: "Series");
            request.RelativePathSamples = new[]
            {
                "Shows/主线剧集 (2024)/Season 01/第01集.mkv",
                "Shows/主线剧集 (2024)/Season 01/第02集.mkv",
            };

            var result = await service.TryResolveTmdbCorrectionAsync(request, CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.ShouldReplace, result.Diagnostic);
            Assert.IsTrue(result.Diagnostic.Contains("ImdbEvidenceDoesNotAlign", StringComparison.Ordinal) || result.Diagnostic.Contains("TvdbEvidenceDoesNotAlign", StringComparison.Ordinal), result.Diagnostic);
            Assert.AreEqual("111", lookupInfo.ProviderIds[MetadataProvider.Tmdb.ToString()]);
            Assert.AreEqual("ttspin111", lookupInfo.ProviderIds[MetadataProvider.Imdb.ToString()]);
            Assert.AreEqual("tvdb-spin-111", lookupInfo.ProviderIds[MetadataProvider.Tvdb.ToString()]);
            Assert.AreEqual("douban-spin-111", lookupInfo.ProviderIds[BaseProvider.DoubanProviderId]);
        }

        [TestMethod]
        public async Task TryResolveTmdbCorrectionAsync_WhenNewTmdbLookupFails_ShouldReturnNoReplacement()
        {
            using var loggerFactory = RecordingLoggerFactory.Create();
            var tmdbApi = this.CreateTmdbApi();
            SeedMissingTmdbMovie(tmdbApi, 222, "zh-CN", string.Empty);
            SeedFindByExternalId(tmdbApi, FindExternalSource.Imdb, "tt1234567", "zh-CN", movieIds: new[] { 222 });
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "222", "Movie")));
            var service = this.CreateService(llmApi, tmdbApi, loggerFactory: loggerFactory);
            var lookupInfo = CreateMovieCorrectionLookupInfo("111", imdbId: "tt1234567");

            var result = await service.TryResolveTmdbCorrectionAsync(CreateCorrectionRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.ShouldReplace, result.Diagnostic);
            Assert.IsTrue(result.Diagnostic.Contains("TMDb movie", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
            Assert.AreEqual("111", lookupInfo.ProviderIds[MetadataProvider.Tmdb.ToString()]);
            loggerFactory.ExpectRejectedReason("CandidateVerificationFailed", "Movie", "TmdbCorrection.Rejected");
        }

        [TestMethod]
        public async Task TryResolveTmdbCorrectionAsync_WhenCandidateMediaTypeMismatches_ShouldReturnNoReplacement()
        {
            var tmdbApi = this.CreateTmdbApi();
            SeedTmdbSeries(tmdbApi, 222, "zh-CN", string.Empty);
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "222", "Series")));
            var service = this.CreateService(llmApi, tmdbApi);
            var lookupInfo = CreateMovieCorrectionLookupInfo("111", imdbId: "tt1234567");

            var result = await service.TryResolveTmdbCorrectionAsync(CreateCorrectionRequest(lookupInfo, mediaType: "Movie"), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.ShouldReplace, result.Diagnostic);
            Assert.IsTrue(result.Diagnostic.Contains("no TMDb candidate", StringComparison.OrdinalIgnoreCase), result.Diagnostic);
            Assert.AreEqual(1, llmApi.Prompts.Count);
        }

        [TestMethod]
        public async Task TryResolveTmdbCorrectionAsync_WhenCandidatesConflict_ShouldReturnNoReplacement()
        {
            using var loggerFactory = RecordingLoggerFactory.Create();
            var tmdbApi = this.CreateTmdbApi();
            SeedTmdbMovie(tmdbApi, 222, "zh-CN", string.Empty);
            SeedTmdbMovie(tmdbApi, 333, "zh-CN", string.Empty);
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "222", "Movie"), CandidateJson("TMDb", "333", "Movie")));
            var service = this.CreateService(llmApi, tmdbApi, loggerFactory: loggerFactory);
            var lookupInfo = CreateMovieCorrectionLookupInfo("111", imdbId: "tt1234567");

            var result = await service.TryResolveTmdbCorrectionAsync(CreateCorrectionRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.ShouldReplace, result.Diagnostic);
            Assert.IsTrue(result.Diagnostic.Contains("AmbiguousCandidates", StringComparison.Ordinal), result.Diagnostic);
            Assert.AreEqual("111", lookupInfo.ProviderIds[MetadataProvider.Tmdb.ToString()]);
            loggerFactory.ExpectRejectedReason("AmbiguousCandidates", "Movie", "TmdbCorrection.Rejected");
        }

        [TestMethod]
        public async Task TryResolveTmdbCorrectionAsync_WhenOldTmdbNotProvenWrong_ShouldReturnNoReplacement()
        {
            using var loggerFactory = RecordingLoggerFactory.Create();
            var tmdbApi = this.CreateTmdbApi();
            SeedTmdbMovie(tmdbApi, 222, "zh-CN", string.Empty);
            SeedFindByExternalId(tmdbApi, FindExternalSource.Imdb, "tt1234567", "zh-CN", movieIds: new[] { 222, 111 });
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "222", "Movie")));
            var service = this.CreateService(llmApi, tmdbApi, loggerFactory: loggerFactory);
            var lookupInfo = CreateMovieCorrectionLookupInfo("111", imdbId: "tt1234567");

            var result = await service.TryResolveTmdbCorrectionAsync(CreateCorrectionRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.ShouldReplace, result.Diagnostic);
            Assert.IsTrue(result.Diagnostic.Contains("ImdbEvidenceDoesNotAlign", StringComparison.Ordinal), result.Diagnostic);
        }

        [DataTestMethod]
        [DataRow(111, DisplayName = "IMDb maps to old TMDb")]
        [DataRow(333, DisplayName = "IMDb maps to third-party TMDb")]
        public async Task TryResolveTmdbCorrectionAsync_WhenImdbEvidenceConflicts_ShouldFailClosedWithoutTvdbFallback(int imdbMappedSeriesId)
        {
            using var loggerFactory = RecordingLoggerFactory.Create();
            var tmdbApi = this.CreateTmdbApi();
            SeedTmdbSeries(tmdbApi, 222, "zh-CN", string.Empty);
            SeedFindByExternalId(tmdbApi, FindExternalSource.Imdb, "tt1234567", "zh-CN", seriesIds: new[] { imdbMappedSeriesId });
            SeedFindByExternalId(tmdbApi, FindExternalSource.TvDb, "tvdb222", "zh-CN", seriesIds: new[] { 222 });
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "222", "Series")));
            var service = this.CreateService(llmApi, tmdbApi, loggerFactory: loggerFactory);
            var lookupInfo = CreateSeriesCorrectionLookupInfo("111", imdbId: "tt1234567", tvdbId: "tvdb222");

            var result = await service.TryResolveTmdbCorrectionAsync(CreateCorrectionRequest(lookupInfo, mediaType: "Series"), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.ShouldReplace, result.Diagnostic);
            Assert.IsTrue(result.Diagnostic.Contains("ImdbEvidenceDoesNotAlign", StringComparison.Ordinal), result.Diagnostic);
            Assert.AreEqual("111", lookupInfo.ProviderIds[MetadataProvider.Tmdb.ToString()]);
            loggerFactory.ExpectRejectedReason("StaleExternalIdConflict", "Series", "TmdbCorrection.Rejected");
        }

        [TestMethod]
        public async Task TryResolveTmdbCorrectionAsync_WhenOnlyTitleYearSimilarityExists_ShouldReturnNoReplacement()
        {
            var tmdbApi = this.CreateTmdbApi();
            SeedTmdbMovie(tmdbApi, 222, "zh-CN", string.Empty);
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "222", "Movie", reason: "title and year match", evidence: "title and year only")));
            var service = this.CreateService(llmApi, tmdbApi);
            var lookupInfo = CreateMovieCorrectionLookupInfo("111");

            var result = await service.TryResolveTmdbCorrectionAsync(CreateCorrectionRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.ShouldReplace, result.Diagnostic);
            Assert.IsTrue(result.Diagnostic.Contains("StrongEvidenceMissing", StringComparison.Ordinal), result.Diagnostic);
        }

        [TestMethod]
        public async Task TryResolveTmdbCorrectionAsync_WhenStrongEvidenceMissing_ShouldLogRejected()
        {
            using var loggerFactory = RecordingLoggerFactory.Create();
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovie(tmdbApi, 222, "zh-CN", string.Empty);
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "222", "Movie", reason: "title and year match", evidence: "title and year only")));
            var service = this.CreateService(llmApi, tmdbApi, loggerFactory: loggerFactory);
            var lookupInfo = CreateMovieCorrectionLookupInfo("111");

            var result = await service.TryResolveTmdbCorrectionAsync(CreateCorrectionRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.ShouldReplace, result.Diagnostic);
            loggerFactory.ExpectRejectedReason("StrongEvidenceMissing", "Movie", "TmdbCorrection.Rejected");
        }

        [TestMethod]
        public async Task TryResolveTmdbCorrectionAsync_WhenTvdbMovieEvidenceUnverifiable_ShouldReturnNoReplacement()
        {
            var tmdbApi = this.CreateTmdbApi();
            SeedTmdbMovie(tmdbApi, 222, "zh-CN", string.Empty);
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "222", "Movie")));
            var service = this.CreateService(llmApi, tmdbApi);
            var lookupInfo = CreateMovieCorrectionLookupInfo("111", tvdbId: "321");

            var result = await service.TryResolveTmdbCorrectionAsync(CreateCorrectionRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.ShouldReplace, result.Diagnostic);
            Assert.IsTrue(result.Diagnostic.Contains("TvdbOwnershipUnverifiable", StringComparison.Ordinal), result.Diagnostic);
        }

        [TestMethod]
        public async Task TryResolveTmdbCorrectionAsync_WhenDoubanHasNoVerifiableOwnership_ShouldReturnNoReplacement()
        {
            var tmdbApi = this.CreateTmdbApi();
            SeedTmdbMovie(tmdbApi, 222, "zh-CN", string.Empty);
            var doubanApi = this.CreateDoubanApi();
            SeedDoubanSubject(doubanApi, "1290000", new DoubanSubject { Sid = "1290000", Category = "电影" });
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "222", "Movie")));
            var service = this.CreateService(llmApi, tmdbApi, doubanApi: doubanApi);
            var lookupInfo = CreateMovieCorrectionLookupInfo("111", doubanId: "1290000");

            var result = await service.TryResolveTmdbCorrectionAsync(CreateCorrectionRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.ShouldReplace, result.Diagnostic);
            Assert.IsTrue(result.Diagnostic.Contains("DoubanOwnershipUnverifiable", StringComparison.Ordinal), result.Diagnostic);
        }

        [DataTestMethod]
        [DataRow(@"{ ""externalIdCandidates"": [] }", "no candidates", DisplayName = "no candidate")]
        [DataRow(@"{ ""externalIdCandidates"": [{ ""provider"": ""TMDb"", ""id"": ""222"", ""mediaType"": ""Movie"", ""confidence"": 0.5, ""reason"": ""weak"", ""evidence"": ""weak"" }] }", "below threshold", DisplayName = "low confidence")]
        public async Task TryResolveTmdbCorrectionAsync_WhenLlmHasNoUsableCandidate_ShouldReturnNoReplacement(string responseJson, string expectedDiagnostic)
        {
            using var loggerFactory = RecordingLoggerFactory.Create();
            var llmApi = new RecordingLlmApi(responseJson);
            var service = this.CreateService(llmApi, this.CreateTmdbApi(), loggerFactory: loggerFactory);
            var lookupInfo = CreateMovieCorrectionLookupInfo("111", imdbId: "tt1234567");

            var result = await service.TryResolveTmdbCorrectionAsync(CreateCorrectionRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.ShouldReplace, result.Diagnostic);
            Assert.IsTrue(result.Diagnostic.Contains(expectedDiagnostic, StringComparison.OrdinalIgnoreCase), result.Diagnostic);
            Assert.AreEqual("111", lookupInfo.ProviderIds[MetadataProvider.Tmdb.ToString()]);
            loggerFactory.ExpectRejectedReason("NoTmdbCandidate", "Movie", "TmdbCorrection.Rejected");
        }

        [DataTestMethod]
        [DataRow(false, true, "LlmTmdbIdCorrectionConfigurationMissing", DisplayName = "correction disabled")]
        [DataRow(true, false, "LlmTmdbIdCorrectionConfigurationMissing", DisplayName = "global LLM disabled")]
        public async Task TryResolveTmdbCorrectionAsync_WhenConfigurationDisabled_ShouldNotCallLlm(bool enableCorrection, bool enableAssist, string expectedDiagnostic)
        {
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "222", "Movie")));
            var service = this.CreateService(llmApi, this.CreateTmdbApi());
            var lookupInfo = CreateMovieCorrectionLookupInfo("111", imdbId: "tt1234567");
            var request = CreateCorrectionRequest(lookupInfo);
            request.Configuration!.EnableLlmTmdbIdCorrection = enableCorrection;
            request.Configuration.EnableLlmAssist = enableAssist;

            var result = await service.TryResolveTmdbCorrectionAsync(request, CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.ShouldReplace, result.Diagnostic);
            Assert.AreEqual(expectedDiagnostic, result.Diagnostic);
            Assert.AreEqual(0, llmApi.Prompts.Count);
        }

        [DataTestMethod]
        [DataRow("Episode")]
        [DataRow("Season")]
        public async Task TryResolveTmdbCorrectionAsync_WhenUnsupportedMediaType_ShouldNotCallLlm(string mediaType)
        {
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "222", "Movie")));
            var service = this.CreateService(llmApi, this.CreateTmdbApi());
            var lookupInfo = new EpisodeInfo { Name = "Episode 1", ProviderIds = new Dictionary<string, string> { [MetadataProvider.Tmdb.ToString()] = "111" } };

            var result = await service.TryResolveTmdbCorrectionAsync(CreateCorrectionRequest(lookupInfo, mediaType: mediaType), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.ShouldReplace, result.Diagnostic);
            Assert.AreEqual("UnsupportedMediaType", result.Diagnostic);
            Assert.AreEqual(0, llmApi.Prompts.Count);
        }

        [TestMethod]
        public async Task TryResolveTmdbCorrectionAsync_WhenNoAllowedTrigger_ShouldNotCallLlm()
        {
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "222", "Movie")));
            var service = this.CreateService(llmApi, this.CreateTmdbApi());
            var lookupInfo = CreateMovieCorrectionLookupInfo("111", imdbId: "tt1234567");
            var request = CreateCorrectionRequest(lookupInfo);
            request.HttpContext = null;

            var result = await service.TryResolveTmdbCorrectionAsync(request, CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.ShouldReplace, result.Diagnostic);
            Assert.AreEqual("ImplicitRefreshRejected", result.Diagnostic);
            Assert.AreEqual(0, llmApi.Prompts.Count);
        }

        [TestMethod]
        public async Task TryResolveTmdbCorrectionAsync_WhenRelativePathContextDisabled_ShouldNotIncludePathsInPrompt()
        {
            var tmdbApi = this.CreateTmdbApi();
            SeedTmdbMovie(tmdbApi, 222, "zh-CN", string.Empty);
            var llmApi = new RecordingLlmApi(ResponseJson(CandidateJson("TMDb", "222", "Movie")));
            var service = this.CreateService(llmApi, tmdbApi);
            var lookupInfo = CreateMovieCorrectionLookupInfo("111", imdbId: "tt1234567");
            lookupInfo.Path = "/mnt/media/Movies/Private Folder/Secret Movie (2020)/Secret.Movie.2020.mkv";
            var request = CreateCorrectionRequest(lookupInfo);
            request.Configuration!.LlmAllowRelativePathContext = false;
            request.RelativePathSamples = new[] { "/mnt/media/Movies/Private Folder/Secret Movie (2020)/sample.mkv", "Movies/Private Folder/Secret Movie (2020)/other.mkv" };

            _ = await service.TryResolveTmdbCorrectionAsync(request, CancellationToken.None).ConfigureAwait(false);

            var prompt = llmApi.Prompts.Single();
            Assert.IsFalse(prompt.Contains("/mnt/media", StringComparison.OrdinalIgnoreCase), prompt);
            Assert.IsFalse(prompt.Contains("Private Folder", StringComparison.Ordinal), prompt);
            Assert.IsFalse(prompt.Contains("Secret.Movie", StringComparison.OrdinalIgnoreCase), prompt);
            Assert.IsFalse(prompt.Contains("sample.mkv", StringComparison.OrdinalIgnoreCase), prompt);
            using var document = JsonDocument.Parse(prompt);
            Assert.AreEqual(0, document.RootElement.GetProperty("SafeRelativePathSamples").GetArrayLength());
        }

        [TestMethod]
        public void ApplyMissingProviderIds_ShouldAddMissingKeepExistingAndRecordSkippedDiagnostics()
        {
            var providerIds = new Dictionary<string, string>
            {
                [MetadataProvider.Imdb.ToString()] = "tt0000001",
                [MetadataProvider.Tvdb.ToString()] = " ",
            };
            var tmdbCandidate = CreateCandidate("TMDb", "27205", "Movie");
            var imdbCandidate = CreateCandidate("IMDb", "tt1375666", "Movie");
            var tvdbCandidate = CreateCandidate("TVDB", "81189", "Series");

            var result = LlmExternalIdResolutionService.ApplyMissingProviderIds(providerIds, new[]
            {
                new LlmExternalIdProviderIdWrite(MetadataProvider.Tmdb.ToString(), "TMDb", "27205", "Movie", tmdbCandidate),
                new LlmExternalIdProviderIdWrite(MetadataProvider.Imdb.ToString(), "IMDb", "tt1375666", "Movie", imdbCandidate),
                new LlmExternalIdProviderIdWrite(MetadataProvider.Tvdb.ToString(), "TVDB", "81189", "Series", tvdbCandidate),
            });

            Assert.AreEqual("27205", providerIds[MetadataProvider.Tmdb.ToString()]);
            Assert.AreEqual("tt0000001", providerIds[MetadataProvider.Imdb.ToString()]);
            Assert.AreEqual("81189", providerIds[MetadataProvider.Tvdb.ToString()]);
            Assert.AreEqual(2, result.AppliedWrites.Count);
            Assert.AreEqual(1, result.SkippedWrites.Count);
            Assert.IsTrue(result.Diagnostics[0].Contains("not overwritten", StringComparison.OrdinalIgnoreCase), result.Diagnostics[0]);
        }

        private LlmExternalIdResolutionService CreateService(RecordingLlmApi llmApi, TmdbApi tmdbApi, TvdbApi? tvdbApi = null, DoubanApi? doubanApi = null, ILlmRequestLimiter? requestLimiter = null, ILoggerFactory? loggerFactory = null)
        {
            var effectiveLoggerFactory = loggerFactory ?? this.loggerFactory;
            return new LlmExternalIdResolutionService(
                llmApi,
                tmdbApi,
                doubanApi ?? new DoubanApi(effectiveLoggerFactory),
                tvdbApi ?? this.CreateTvdbApi(effectiveLoggerFactory),
                new LlmAssistTriggerPolicy(),
                new LlmTmdbIdCorrectionTriggerPolicy(),
                new LlmExternalIdCandidateValidator(),
                requestLimiter,
                effectiveLoggerFactory.CreateLogger<LlmExternalIdResolutionService>());
        }

        private TmdbApi CreateTmdbApi()
        {
            return this.CreateTmdbApi(this.loggerFactory);
        }

        private TmdbApi CreateTmdbApi(ILoggerFactory loggerFactory)
        {
            return new TmdbApi(loggerFactory);
        }

        private TvdbApi CreateTvdbApi()
        {
            return this.CreateTvdbApi(this.loggerFactory);
        }

        private TvdbApi CreateTvdbApi(ILoggerFactory loggerFactory)
        {
            return new TvdbApi(loggerFactory);
        }

        private DoubanApi CreateDoubanApi()
        {
            return new DoubanApi(this.loggerFactory);
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
                    $"Expected captured rejection reason {reasonCode} for {mediaType} on {eventName}.");
            }

            public void ExpectAppliedReason(string reasonCode, string mediaType, string eventName = "TmdbCorrection.Applied")
            {
                Assert.IsTrue(
                    this.provider.Entries.Any(entry =>
                        entry.Level == LogLevel.Information
                        && string.Equals(entry.EventId.Name, eventName, StringComparison.Ordinal)
                        && entry.State.TryGetValue("ReasonCode", out var actualReason)
                        && string.Equals(actualReason?.ToString(), reasonCode, StringComparison.Ordinal)
                        && entry.State.TryGetValue("MediaType", out var actualMediaType)
                        && string.Equals(actualMediaType?.ToString(), mediaType, StringComparison.Ordinal)),
                    $"Expected captured applied reason {reasonCode} for {mediaType} on {eventName}.");
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

        private static LlmExternalIdResolutionRequest CreateRequest(ItemLookupInfo lookupInfo, string? mediaType = null)
        {
            return new LlmExternalIdResolutionRequest
            {
                Configuration = CreateConfiguration(),
                LookupInfo = lookupInfo,
                MediaType = mediaType ?? "Movie",
                Semantic = DefaultScraperSemantic.UserRefresh,
                HttpContext = CreateRefreshContext(),
                LibraryRoots = new[] { "/mnt/media" },
            };
        }

        private static EpisodeInfo CreateEpisodeLookupInfo(string? seriesTvdbId, int seasonNumber, int episodeNumber)
        {
            var seriesProviderIds = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(seriesTvdbId))
            {
                seriesProviderIds[MetadataProvider.Tvdb.ToString()] = seriesTvdbId;
            }

            return new EpisodeInfo
            {
                Name = "Episode 1",
                MetadataLanguage = "zh-CN",
                ParentIndexNumber = seasonNumber,
                IndexNumber = episodeNumber,
                ProviderIds = new Dictionary<string, string>(),
                SeriesProviderIds = seriesProviderIds,
            };
        }

        private static PluginConfiguration CreateConfiguration()
        {
            return new PluginConfiguration
            {
                EnableLlmAssist = true,
                EnableLlmTmdbIdCorrection = true,
                LlmBaseUrl = "http://127.0.0.1:11434/v1",
                LlmModel = "test-model",
                LlmApiKey = "test-key",
                LlmConfidenceThreshold = 0.75,
            };
        }

        private static LlmTmdbIdCorrectionRequest CreateCorrectionRequest(ItemLookupInfo lookupInfo, string? mediaType = null)
        {
            return new LlmTmdbIdCorrectionRequest
            {
                Configuration = CreateConfiguration(),
                LookupInfo = lookupInfo,
                MediaType = mediaType ?? "Movie",
                OldTmdbId = lookupInfo.ProviderIds != null && lookupInfo.ProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var oldTmdbId) ? oldTmdbId : null,
                Semantic = DefaultScraperSemantic.UserRefresh,
                HttpContext = CreateRefreshContext(),
                LibraryRoots = new[] { "/mnt/media" },
            };
        }

        private static MovieInfo CreateMovieCorrectionLookupInfo(string oldTmdbId, string? imdbId = null, string? tvdbId = null, string? doubanId = null)
        {
            var providerIds = new Dictionary<string, string>
            {
                [MetadataProvider.Tmdb.ToString()] = oldTmdbId,
            };
            if (!string.IsNullOrWhiteSpace(imdbId))
            {
                providerIds[MetadataProvider.Imdb.ToString()] = imdbId;
            }

            if (!string.IsNullOrWhiteSpace(tvdbId))
            {
                providerIds[MetadataProvider.Tvdb.ToString()] = tvdbId;
            }

            if (!string.IsNullOrWhiteSpace(doubanId))
            {
                providerIds[BaseProvider.DoubanProviderId] = doubanId;
            }

            return new MovieInfo
            {
                Name = "Correction Movie",
                Year = 2020,
                MetadataLanguage = "zh-CN",
                ProviderIds = providerIds,
            };
        }

        private static SeriesInfo CreateSeriesCorrectionLookupInfo(string oldTmdbId, string? imdbId = null, string? tvdbId = null, string? doubanId = null)
        {
            var providerIds = new Dictionary<string, string>
            {
                [MetadataProvider.Tmdb.ToString()] = oldTmdbId,
            };
            if (!string.IsNullOrWhiteSpace(imdbId))
            {
                providerIds[MetadataProvider.Imdb.ToString()] = imdbId;
            }

            if (!string.IsNullOrWhiteSpace(tvdbId))
            {
                providerIds[MetadataProvider.Tvdb.ToString()] = tvdbId;
            }

            if (!string.IsNullOrWhiteSpace(doubanId))
            {
                providerIds[BaseProvider.DoubanProviderId] = doubanId;
            }

            return new SeriesInfo
            {
                Name = "Correction Series",
                Year = 2020,
                MetadataLanguage = "zh-CN",
                ProviderIds = providerIds,
            };
        }

        private static DefaultHttpContext CreateRefreshContext()
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = "/Items/11111111-1111-1111-1111-111111111111/Refresh";
            context.Request.QueryString = new QueryString("?metadataRefreshMode=FullRefresh&replaceAllMetadata=false");
            return context;
        }

        private static LlmExternalIdCandidate CreateCandidate(string provider, string id, string mediaType)
        {
            return new LlmExternalIdCandidate
            {
                Provider = provider,
                Id = id,
                MediaType = mediaType,
                Confidence = 0.9,
                Reason = "title and year match",
                Evidence = "filename match",
            };
        }

        private static string ResponseJson(params string[] candidates)
        {
            return "{\"externalIdCandidates\":[" + string.Join(",", candidates) + "]}";
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

        private static void AssertPromptUsesExternalIdSchema(string prompt)
        {
            using var document = JsonDocument.Parse(prompt);
            var root = document.RootElement;
            Assert.AreEqual("Resolve public external ID candidates for this media item.", root.GetProperty("Task").GetString());
            Assert.IsTrue(root.GetProperty("OutputSchema").GetString()!.Contains("externalIdCandidates", StringComparison.Ordinal), prompt);
            Assert.IsFalse(prompt.Contains("MetaSharkID", StringComparison.OrdinalIgnoreCase), prompt);
        }

        private static void SeedTmdbMovie(TmdbApi tmdbApi, int tmdbId, string language, string imageLanguages)
        {
            GetTmdbMemoryCache(tmdbApi).Set(
                $"movie-{tmdbId.ToString(CultureInfo.InvariantCulture)}-{language}-{imageLanguages}",
                new TmdbMovie { Id = tmdbId },
                TimeSpan.FromMinutes(5));
        }

        private static void SeedMissingTmdbMovie(TmdbApi tmdbApi, int tmdbId, string language, string imageLanguages)
        {
            GetTmdbMemoryCache(tmdbApi).Set<TmdbMovie?>(
                $"movie-{tmdbId.ToString(CultureInfo.InvariantCulture)}-{language}-{imageLanguages}",
                null,
                TimeSpan.FromMinutes(5));
        }

        private static void SeedTmdbSeries(TmdbApi tmdbApi, int tmdbId, string language, string imageLanguages)
        {
            GetTmdbMemoryCache(tmdbApi).Set(
                $"series-{tmdbId.ToString(CultureInfo.InvariantCulture)}-{language}-{imageLanguages}",
                new TmdbTvShow { Id = tmdbId },
                TimeSpan.FromMinutes(5));
        }

        private static void SeedFindByExternalId(TmdbApi tmdbApi, FindExternalSource source, string externalId, string language, int[]? movieIds = null, int[]? seriesIds = null)
        {
            GetTmdbMemoryCache(tmdbApi).Set(
                $"find-{source.ToString()}-{externalId}-{language}",
                new FindContainer
                {
                    MovieResults = (movieIds ?? Array.Empty<int>()).Select(id => new SearchMovie { Id = id }).ToList(),
                    TvResults = (seriesIds ?? Array.Empty<int>()).Select(id => new SearchTv { Id = id }).ToList(),
                },
                TimeSpan.FromMinutes(5));
        }

        private static void SeedDoubanSubject(DoubanApi doubanApi, string sid, DoubanSubject? subject)
        {
            GetDoubanMemoryCache(doubanApi).Set($"movie_{sid}", subject, TimeSpan.FromMinutes(5));
        }

        private TvdbApi CreateTvdbApiWithResponses(string episodesJson, HttpStatusCode episodeResponseStatusCode = HttpStatusCode.OK)
        {
            var api = this.CreateTvdbApi();
            ReplaceHttpClient(
                api,
                new HttpClient(new RoutingHttpMessageHandler(request => CreateTvdbResponse(request, episodesJson, episodeResponseStatusCode)), disposeHandler: true)
                {
                    BaseAddress = new Uri("https://api4.thetvdb.com/v4/"),
                    Timeout = TimeSpan.FromSeconds(10),
                });
            return api;
        }

        private static string CreateTvdbEpisodesResponse(int? episodeId = null, int? seasonNumber = null, int? episodeNumber = null)
        {
            var episodes = episodeId.HasValue
                ? new[]
                {
                    new
                    {
                        id = episodeId,
                        seasonNumber,
                        number = episodeNumber,
                        aired = "2024-01-01",
                    },
                }
                : Array.Empty<object>();
            return JsonSerializer.Serialize(new
            {
                data = new { episodes },
                links = new { next = (string?)null },
            });
        }

        private static HttpResponseMessage CreateTvdbResponse(HttpRequestMessage request, string episodesJson, HttpStatusCode episodeResponseStatusCode)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/login", StringComparison.Ordinal) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":{\"token\":\"test-token\"}}", Encoding.UTF8, "application/json"),
                };
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath.EndsWith("/series/321/episodes/official/zho", StringComparison.Ordinal) == true)
            {
                return new HttpResponseMessage(episodeResponseStatusCode)
                {
                    Content = new StringContent(episodesJson, Encoding.UTF8, "application/json"),
                };
            }

            throw new InvalidOperationException($"未预期的 TVDB 请求: {request.Method} {request.RequestUri}");
        }

        private static void ReplaceHttpClient(TvdbApi api, HttpClient replacement)
        {
            var httpClientField = typeof(TvdbApi).GetField("httpClient", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(httpClientField, "TvdbApi.httpClient 未定义");

            var originalClient = httpClientField!.GetValue(api) as HttpClient;
            Assert.IsNotNull(originalClient, "TvdbApi.httpClient 不是有效的 HttpClient");

            httpClientField.SetValue(api, replacement);
            originalClient!.Dispose();
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

            Assert.Fail("Could not initialize MetaSharkPlugin configuration for tests.");
        }

        private static MemoryCache GetTmdbMemoryCache(TmdbApi tmdbApi)
        {
            var memoryCacheField = typeof(TmdbApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(memoryCacheField, "TmdbApi.memoryCache 未找到");
            var memoryCache = memoryCacheField!.GetValue(tmdbApi) as MemoryCache;
            Assert.IsNotNull(memoryCache, "TmdbApi.memoryCache 不是有效的 MemoryCache");
            return memoryCache!;
        }

        private static MemoryCache GetDoubanMemoryCache(DoubanApi doubanApi)
        {
            var memoryCacheField = typeof(DoubanApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(memoryCacheField, "DoubanApi.memoryCache 未找到");
            var memoryCache = memoryCacheField!.GetValue(doubanApi) as MemoryCache;
            Assert.IsNotNull(memoryCache, "DoubanApi.memoryCache 不是有效的 MemoryCache");
            return memoryCache!;
        }

        private sealed class RecordingLlmApi : ILlmApi
        {
            private readonly string contentJson;

            public RecordingLlmApi(string contentJson)
            {
                this.contentJson = contentJson;
            }

            public List<string> Prompts { get; } = new();

            public Task<LlmApiResult> CompleteAsync(string prompt, CancellationToken cancellationToken)
            {
                this.Prompts.Add(prompt);
                return Task.FromResult(LlmApiResult.Succeeded(this.contentJson));
            }
        }

        private sealed class RoutingHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> responder;

            public RoutingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            {
                this.responder = responder;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(this.responder(request));
            }
        }
    }
}
