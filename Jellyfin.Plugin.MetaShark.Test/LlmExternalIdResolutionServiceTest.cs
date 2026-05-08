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
        public async Task ResolveAsync_WhenProviderIdExists_ShouldNotOverwrite()
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

            var result = await service.ResolveAsync(CreateRequest(lookupInfo), CancellationToken.None).ConfigureAwait(false);

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

        private LlmExternalIdResolutionService CreateService(RecordingLlmApi llmApi, TmdbApi tmdbApi, TvdbApi? tvdbApi = null, ILlmRequestLimiter? requestLimiter = null)
        {
            return new LlmExternalIdResolutionService(
                llmApi,
                tmdbApi,
                new DoubanApi(this.loggerFactory),
                tvdbApi ?? this.CreateTvdbApi(),
                new LlmAssistTriggerPolicy(),
                new LlmExternalIdCandidateValidator(),
                requestLimiter);
        }

        private TmdbApi CreateTmdbApi()
        {
            return new TmdbApi(this.loggerFactory);
        }

        private TvdbApi CreateTvdbApi()
        {
            return new TvdbApi(this.loggerFactory);
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
                LlmBaseUrl = "http://127.0.0.1:11434/v1",
                LlmModel = "test-model",
                LlmApiKey = "test-key",
                LlmConfidenceThreshold = 0.75,
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

        private static string CandidateJson(string provider, string id, string mediaType, double confidence = 0.9)
        {
            return JsonSerializer.Serialize(new
            {
                provider,
                id,
                mediaType,
                confidence,
                reason = "title and year match",
                evidence = "filename match",
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
