using System.Collections;
using System.Globalization;
using System.Reflection;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Core;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Providers.Llm;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using TMDbLib.Client;
using TMDbLib.Objects.General;
using TMDbLib.Objects.Search;
using TmdbGenre = TMDbLib.Objects.General.Genre;
using TmdbMovie = TMDbLib.Objects.Movies.Movie;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [DoNotParallelize]
    public class MovieProviderLlmAssistTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-movie-provider-llm-tests");
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
        public async Task GetMetadata_DoesNotCallLlm_WhenConfigurationDisabled()
        {
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("disabled-douban", "Disabled Movie", 2024);
            SeedDoubanSearchResults(doubanApi, "Disabled Movie", subject);
            SeedDoubanSubject(doubanApi, subject);
            var llm = CreateSuccessfulLlm("Ignored Movie", 2024);
            var provider = CreateProvider(loggerFactory, doubanApi: doubanApi, llmMetadataAssistService: llm);

            var result = await provider.GetMetadata(CreateMovieInfo("Disabled Movie", "/mnt/media/Movies/Disabled Movie/Disabled Movie.mkv", 2024), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, llm.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("disabled-douban", result.Item!.GetProviderId(BaseProvider.DoubanProviderId));
        }

        [TestMethod]
        public async Task GetMetadata_DoesNotCallLlm_WhenAutomaticRefresh()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("automatic-douban", "Automatic Movie", 2024);
            SeedDoubanSearchResults(doubanApi, "Automatic Movie", subject);
            SeedDoubanSubject(doubanApi, subject);
            var llm = CreateSuccessfulLlm("Ignored Movie", 2024);
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateAutomaticRefreshContextAccessor(),
                doubanApi: doubanApi,
                llmMetadataAssistService: llm);
            var info = CreateMovieInfo("Automatic Movie", "/mnt/media/Movies/Automatic Movie/Automatic Movie.mkv", 2024);
            info.IsAutomated = true;

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, llm.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("automatic-douban", result.Item!.GetProviderId(BaseProvider.DoubanProviderId));
        }

        [TestMethod]
        public async Task GetMetadata_DoesNotCallMetadataAssist_WhenTextCompletionDisabled()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration(allowTextCompletion: false));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("text-disabled-douban", "Text Disabled Movie", 2024);
            SeedDoubanSearchResults(doubanApi, "Text Disabled Movie", subject);
            SeedDoubanSubject(doubanApi, subject);
            var llm = CreateSuccessfulLlm("Ignored Text Completion Movie", 2024);
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                llmMetadataAssistService: llm);

            var result = await provider.GetMetadata(CreateMovieInfo("Text Disabled Movie", "/mnt/media/Movies/Text Disabled Movie/Text Disabled Movie.mkv", 2024), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, llm.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("text-disabled-douban", result.Item!.GetProviderId(BaseProvider.DoubanProviderId));
        }

        [TestMethod]
        public async Task GetMetadata_CallsLlmOnceForManualMatch_AndSendsSafeRelativePath()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("manual-douban", "Correct Manual Movie", 2024);
            SeedDoubanSearchResults(doubanApi, "Correct Manual Movie", subject);
            SeedDoubanSubject(doubanApi, subject);
            var llm = CreateSuccessfulLlm("Correct Manual Movie", 2024);
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                llmMetadataAssistService: llm);
            var info = CreateMovieInfo("Wrong Manual Movie", "/mnt/media/Movies/Correct Manual Movie/Correct Manual Movie.mkv", 2024);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, llm.Requests.Count);
            Assert.AreEqual(DefaultScraperSemantic.ManualMatch, llm.Requests[0].Semantic);
            Assert.AreEqual("Movie", llm.Requests[0].MediaType);
            Assert.AreEqual("Movies/Correct Manual Movie/Correct Manual Movie.mkv", llm.Requests[0].LookupInfo!.Path);
            LlmProviderFlowTestHelpers.AssertNoSensitiveContent(llm.Requests[0]);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("manual-douban", result.Item!.GetProviderId(BaseProvider.DoubanProviderId));
        }

        [TestMethod]
        public async Task GetMetadata_CallsLlmOnceForExplicitSearchMissingRefresh()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            SeedDoubanSearchResults(doubanApi, "Explicit Search Missing", Array.Empty<DoubanSubject>());
            SeedDoubanSearchResults(doubanApi, "Explicit Hint Movie", Array.Empty<DoubanSubject>());
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovieSearchResults(tmdbApi, "Explicit Hint Movie", 2025, "zh-CN", CreateSearchMovie(4101, "Explicit Hint Movie", 2025));
            SeedTmdbMovie(tmdbApi, 4101, "zh-CN", CreateTmdbMovie(4101, "Explicit Hint Movie", 2025, overview: "TMDb explicit hint overview"));
            var llm = CreateSuccessfulLlm("Explicit Hint Movie", 2025);
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateExplicitSearchMissingContextAccessor(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmMetadataAssistService: llm);

            var result = await provider.GetMetadata(CreateMovieInfo("Explicit Search Missing", "/mnt/media/Movies/Explicit Hint Movie/Explicit Hint Movie.mkv", 2025), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, llm.Requests.Count);
            Assert.AreEqual(DefaultScraperSemantic.UserRefresh, llm.Requests[0].Semantic);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("4101", result.Item!.GetProviderId(MetadataProvider.Tmdb));
        }

        [TestMethod]
        public async Task GetMetadata_FallsBackToDeterministicMatch_WhenLlmFails()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("fallback-douban", "Fallback Movie", 2023);
            SeedDoubanSearchResults(doubanApi, "Fallback Movie", subject);
            SeedDoubanSubject(doubanApi, subject);
            var llm = new LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService();
            llm.EnqueueResult(LlmScrapingAssistResult.Failed("test failure", new LlmPromptContext { MediaType = "Movie", RelativePath = "Movies/Fallback Movie/Fallback Movie.mkv" }));
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                llmMetadataAssistService: llm);

            var result = await provider.GetMetadata(CreateMovieInfo("Fallback Movie", "/mnt/media/Movies/Fallback Movie/Fallback Movie.mkv", 2023), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, llm.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("fallback-douban", result.Item!.GetProviderId(BaseProvider.DoubanProviderId));
        }

        [TestMethod]
        public async Task GetMetadata_DoesNotCallLlm_WhenMovieExtraReturnsEarly()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var llm = CreateSuccessfulLlm("Ignored Extra", 2024);
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                llmMetadataAssistService: llm);
            var info = CreateMovieInfo(
                "[VCB-Studio] Spice and Wolf NCOP",
                "/mnt/media/Movies/Spice and Wolf/[VCB-Studio] Spice and Wolf [NCOP][Ma10p_1080p][x265_flac].mkv",
                null);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, llm.Requests.Count);
            Assert.IsFalse(result.HasMetadata);
            Assert.IsNull(result.Item);
        }

        [TestMethod]
        public async Task GetMetadata_UsesLlmHintsForTmdbSearch_AndPreservesProviderIdsDuringTextCompletion()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration(allowTextCompletion: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            SeedDoubanSearchResults(doubanApi, "Text Completion Bad Title", Array.Empty<DoubanSubject>());
            SeedDoubanSearchResults(doubanApi, "Authoritative Hint Movie", Array.Empty<DoubanSubject>());
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovieSearchResults(tmdbApi, "Authoritative Hint Movie", 2026, "zh-CN", CreateSearchMovie(5001, "Authoritative Hint Movie", 2026));
            SeedTmdbMovie(tmdbApi, 5001, "zh-CN", CreateTmdbMovie(5001, "Authoritative Hint Movie", 2026, originalTitle: string.Empty, overview: string.Empty));
            var llm = CreateSuccessfulLlm("Authoritative Hint Movie", 2026, originalTitle: "LLM Original Title", overview: "LLM allowed overview");
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmMetadataAssistService: llm);

            var result = await provider.GetMetadata(CreateMovieInfo("Text Completion Bad Title", "/mnt/media/Movies/Authoritative Hint Movie/Authoritative Hint Movie.mkv", 2026), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, llm.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("Authoritative Hint Movie", result.Item!.Name, "LLM 不能覆盖权威来源已有标题。 ");
            Assert.AreEqual("LLM Original Title", result.Item.OriginalTitle);
            Assert.AreEqual("LLM allowed overview", result.Item.Overview);
            var expectedProviderIds = new Dictionary<string, string>
            {
                [MetadataProvider.Tmdb.ToString()] = "5001",
                [MetaSharkPlugin.ProviderId] = "Tmdb_5001",
            };
            LlmProviderFlowTestHelpers.AssertProviderIdsUnchanged(expectedProviderIds, result.Item.ProviderIds);
        }

        [TestMethod]
        public async Task GetMetadata_UsesLlmDoubanHintsWithoutWritingProviderIdsDirectly()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            SeedDoubanSearchResults(doubanApi, "Bad Douban Title", Array.Empty<DoubanSubject>());
            var hintedSubject = CreateDoubanSubject("hinted-douban", "Hinted Douban Movie", 2027);
            SeedDoubanSearchResults(doubanApi, "Hinted Douban Movie", hintedSubject);
            SeedDoubanSubject(doubanApi, hintedSubject);
            var llm = CreateSuccessfulLlm("Hinted Douban Movie", 2027);
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                llmMetadataAssistService: llm);

            var result = await provider.GetMetadata(CreateMovieInfo("Bad Douban Title", "/mnt/media/Movies/Hinted Douban Movie/Hinted Douban Movie.mkv", 2027), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, llm.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("hinted-douban", result.Item!.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Douban_hinted-douban", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
        }

        [TestMethod]
        public async Task GetMetadata_UsesLlmHintToCorrectDeterministicDoubanMismatch_ByRequeryingDouban()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var wrongSubject = CreateDoubanSubject("wrong-douban", "Unrelated Wrong Title", 1999, overview: "wrong overview");
            var correctedSubject = CreateDoubanSubject("corrected-douban", "Correct Mismatch Movie", 2028, overview: "correct overview");
            SeedDoubanSubject(doubanApi, wrongSubject);
            SeedDoubanSubject(doubanApi, correctedSubject);
            SeedDoubanSearchResults(doubanApi, "Correct Mismatch Movie", correctedSubject);
            var llm = CreateSuccessfulLlm("Correct Mismatch Movie", 2028);
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                llmMetadataAssistService: llm);
            var info = CreateMovieInfo("Correct Mismatch Movie", "/mnt/media/Movies/Correct Mismatch Movie/Correct Mismatch Movie.mkv", 2028);
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "wrong-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_wrong-douban",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, llm.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("corrected-douban", result.Item!.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Douban_corrected-douban", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual("correct overview", result.Item.Overview);
        }


        [TestMethod]
        public async Task GetMetadata_WhenColdStartLlmTextHintsThenVerifiedTmdbCandidate_UsesVerifiedTmdbCandidate()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            SeedDoubanSearchResults(doubanApi, "Wrong Cold Movie", Array.Empty<DoubanSubject>());
            SeedDoubanSearchResults(doubanApi, "Correct Cold Movie", Array.Empty<DoubanSubject>());
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovieSearchResults(tmdbApi, "Correct Cold Movie", 2029, "zh-CN", Array.Empty<SearchMovie>());
            SeedTmdbMovie(tmdbApi, 6101, "zh-CN", CreateTmdbMovie(6101, "Verified Cold Movie", 2029, overview: "TMDb verified cold overview"));
            var textLlm = CreateSuccessfulLlm("Correct Cold Movie", 2029);
            var externalIdResolver = CreateExternalIdResolverWithWrites(CreateProviderIdWrite(MetadataProvider.Tmdb.ToString(), "TMDb", "6101"));
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmMetadataAssistService: textLlm,
                llmExternalIdResolutionService: externalIdResolver);

            var result = await provider.GetMetadata(CreateMovieInfo("Wrong Cold Movie", "/mnt/media/Movies/Correct Cold Movie/Correct Cold Movie.mkv", 2029), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, textLlm.Requests.Count);
            Assert.AreEqual(1, externalIdResolver.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("6101", result.Item!.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("Tmdb_6101", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual("Verified Cold Movie", result.Item.Name);
            Assert.AreEqual("TMDb verified cold overview", result.Item.Overview);
        }

        [TestMethod]
        public async Task GetMetadata_WhenDoubanExistsButTmdbMissing_UsesVerifiedLlmTmdbCandidate()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("douban-without-tmdb", "Douban Existing Movie", 2030);
            SeedDoubanSubject(doubanApi, subject);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovie(tmdbApi, 6201, "zh-CN", CreateTmdbMovie(6201, "TMDb Supplemental Movie", 2030));
            var externalIdResolver = CreateExternalIdResolverWithWrites(CreateProviderIdWrite(MetadataProvider.Tmdb.ToString(), "TMDb", "6201"));
            var persistenceService = new Mock<ILlmTmdbCorrectionMapPersistenceService>();
            persistenceService
                .Setup(x => x.TryUpsertDoubanCompletionAsync(nameof(Movie), "douban-without-tmdb", "6201", It.IsAny<CancellationToken>()))
                .ReturnsAsync(LlmTmdbCorrectionMapPersistenceResult.SavedResult(string.Empty, "movie:douban:douban-without-tmdb=tmdb:6201"));
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateExplicitSearchMissingContextAccessor(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdResolver,
                llmTmdbCorrectionMapPersistenceService: persistenceService.Object);
            var info = CreateMovieInfo("Douban Existing Movie", "/mnt/media/Movies/Douban Existing Movie/Douban Existing Movie.mkv", 2030);
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "douban-without-tmdb",
                [MetaSharkPlugin.ProviderId] = "Douban_douban-without-tmdb",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdResolver.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("Douban Existing Movie", result.Item!.Name);
            Assert.AreEqual("Douban Existing Movie overview", result.Item.Overview);
            Assert.AreEqual("douban-without-tmdb", result.Item!.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Douban_douban-without-tmdb", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual("6201", result.Item.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual(0, externalIdResolver.CorrectionRequests.Count);
            persistenceService.Verify(x => x.TryUpsertDoubanCorrectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            persistenceService.Verify(x => x.TryUpsertDoubanCompletionAsync(nameof(Movie), "douban-without-tmdb", "6201", It.IsAny<CancellationToken>()), Times.Once);
            Assert.AreEqual(string.Empty, MetaSharkPlugin.Instance!.Configuration.LlmTmdbCorrectionMap);
        }

        [TestMethod]
        public async Task GetMetadata_WhenOverwriteRefreshHasDoubanPrimaryAndCompletedTmdbId_PreservesTmdbIdWithoutResolver()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration(enableTmdbCorrection: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("overwrite-completed-douban", "Overwrite Completed Movie", 2035);
            SeedDoubanSubject(doubanApi, subject);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovie(tmdbApi, 6701, "zh-CN", CreateTmdbMovie(6701, "TMDb Should Stay Supplemental", 2035));
            var externalIdResolver = CreateExternalIdResolverWithWrites(CreateProviderIdWrite(MetadataProvider.Tmdb.ToString(), "TMDb", "9999"));
            externalIdResolver.EnqueueExistingProviderDecision(LlmAssistTriggerDecision.Rejected("ExistingProviderIdsConsistent"));
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateExplicitRefreshContextAccessor(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture), replaceAllMetadata: true),
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdResolver);
            var info = CreateMovieInfo("Overwrite Completed Movie", "/mnt/media/Movies/Overwrite Completed Movie/Overwrite Completed Movie.mkv", 2035);
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "overwrite-completed-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_overwrite-completed-douban",
                [MetadataProvider.Tmdb.ToString()] = "6701",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("Overwrite Completed Movie", result.Item!.Name);
            Assert.AreEqual("Overwrite Completed Movie overview", result.Item.Overview);
            Assert.AreEqual("overwrite-completed-douban", result.Item.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Douban_overwrite-completed-douban", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual("6701", result.Item.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual(1, externalIdResolver.ExistingProviderDecisionRequests.Count);
            Assert.AreEqual(0, externalIdResolver.Requests.Count);
            Assert.AreEqual(0, externalIdResolver.CorrectionRequests.Count);
        }

        [TestMethod]
        public async Task GetMetadata_WhenResolverReturnsTvdbMovieWrite_IgnoresWriteAndKeepsProviderIdsStable()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("tvdb-ignored-douban", "TVDB Ignored Movie", 2031);
            SeedDoubanSubject(doubanApi, subject);
            var tvdbWrite = new LlmExternalIdProviderIdWrite(
                MetadataProvider.Tvdb.ToString(),
                "TVDB",
                "81189",
                "Movie",
                CreateCandidate("TVDB", "81189", "Movie"));
            var externalIdResolver = CreateExternalIdResolverWithWrites(tvdbWrite);
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateExplicitSearchMissingContextAccessor(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                llmExternalIdResolutionService: externalIdResolver);
            var info = CreateMovieInfo("TVDB Ignored Movie", "/mnt/media/Movies/TVDB Ignored Movie/TVDB Ignored Movie.mkv", 2031);
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "tvdb-ignored-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_tvdb-ignored-douban",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdResolver.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("tvdb-ignored-douban", result.Item!.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Douban_tvdb-ignored-douban", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.IsNull(result.Item.GetProviderId(MetadataProvider.Tvdb));
        }

        [TestMethod]
        public async Task GetMetadata_StaleExternalIdConflict_WhenDoubanMetadataIsSemanticallyConsistent_DoesNotCallExternalIdResolver()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("consistent-douban", "Consistent Existing Movie", 2033);
            SeedDoubanSubject(doubanApi, subject);
            var externalIdResolver = new LlmProviderFlowTestHelpers.RecordingLlmExternalIdResolutionService();
            externalIdResolver.EnqueueExistingProviderDecision(LlmAssistTriggerDecision.Rejected("ExistingProviderIdsConsistent"));
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateExplicitSearchMissingContextAccessor(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                llmExternalIdResolutionService: externalIdResolver);
            var info = CreateMovieInfo("Consistent Existing Movie", "/mnt/media/Movies/Consistent Existing Movie/Consistent Existing Movie.mkv", 2033);
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "consistent-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_consistent-douban",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdResolver.ExistingProviderDecisionRequests.Count);
            Assert.AreEqual(0, externalIdResolver.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("consistent-douban", result.Item!.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.Item.GetProviderId(MetadataProvider.Tmdb)));
        }

        [TestMethod]
        public async Task GetMetadata_StaleExternalIdConflict_WhenDoubanMetadataIsSemanticallyStale_CallsExternalIdResolver()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("stale-douban", "Wrong Existing Movie", 1999);
            SeedDoubanSubject(doubanApi, subject);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovie(tmdbApi, 6331, "zh-CN", CreateTmdbMovie(6331, "Resolved Existing Movie", 2034));
            var externalIdResolver = CreateExternalIdResolverWithWrites(CreateProviderIdWrite(MetadataProvider.Tmdb.ToString(), "TMDb", "6331"));
            externalIdResolver.EnqueueExistingProviderDecision(LlmAssistTriggerDecision.Allowed("StaleExternalIdConflict"));
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateExplicitSearchMissingContextAccessor(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdResolver);
            var info = CreateMovieInfo("Resolved Existing Movie", "/mnt/media/Movies/Resolved Existing Movie/Resolved Existing Movie.mkv", 2034);
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "stale-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_stale-douban",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdResolver.ExistingProviderDecisionRequests.Count);
            Assert.AreEqual(1, externalIdResolver.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("stale-douban", result.Item!.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.AreEqual("6331", result.Item.GetProviderId(MetadataProvider.Tmdb));
        }

        [TestMethod]
        public async Task GetMetadata_WhenExistingTmdbIdPresentAndCorrectionSwitchOff_DoesNotCallExternalIdResolverOrOverwrite()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("existing-tmdb-douban", "Existing TMDb Movie", 2032);
            SeedDoubanSubject(doubanApi, subject);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovie(tmdbApi, 6301, "zh-CN", CreateTmdbMovie(6301, "Existing TMDb Supplemental Movie", 2032));
            var externalIdResolver = CreateExternalIdResolverWithWrites(CreateProviderIdWrite(MetadataProvider.Tmdb.ToString(), "TMDb", "9999"));
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateExplicitSearchMissingContextAccessor(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdResolver);
            var info = CreateMovieInfo("Existing TMDb Movie", "/mnt/media/Movies/Existing TMDb Movie/Existing TMDb Movie.mkv", 2032);
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "existing-tmdb-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_existing-tmdb-douban",
                [MetadataProvider.Tmdb.ToString()] = "6301",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, externalIdResolver.Requests.Count);
            Assert.AreEqual(0, externalIdResolver.CorrectionRequests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("6301", result.Item!.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("existing-tmdb-douban", result.Item.GetProviderId(BaseProvider.DoubanProviderId));
        }

        [TestMethod]
        public async Task GetMetadata_WhenExistingTmdbIdPresentAndCorrectionDisabled_DoesNotCallCorrectionOrOverwrite()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration(enableTmdbCorrection: false));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("correction-off-douban", "Correction Off Movie", 2036);
            SeedDoubanSubject(doubanApi, subject);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovie(tmdbApi, 111, "zh-CN", CreateTmdbMovie(111, "Old TMDb Movie", 2036));
            var externalIdResolver = new LlmProviderFlowTestHelpers.RecordingLlmExternalIdResolutionService();
            externalIdResolver.EnqueueExistingProviderDecision(LlmAssistTriggerDecision.Allowed("StaleExternalIdConflict"));
            externalIdResolver.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.Verified("222", "test verified replacement"));
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdResolver);
            var info = CreateMovieInfo("Correction Off Movie", "/mnt/media/Movies/Correction Off Movie/Correction Off Movie.mkv", 2036);
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "correction-off-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_correction-off-douban",
                [MetadataProvider.Tmdb.ToString()] = "111",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, externalIdResolver.CorrectionRequests.Count);
            Assert.AreEqual(0, externalIdResolver.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("111", result.Item!.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("correction-off-douban", result.Item.GetProviderId(BaseProvider.DoubanProviderId));
        }

        [TestMethod]
        public async Task GetMetadata_LlmDoubanCorrectionMapWithPersistenceDisabled_StillUsesDefaultDoubanBranch()
        {
            var configuration = CreateLlmConfiguration();
            configuration.EnableLlmTmdbCorrectionPersistence = false;
            configuration.LlmTmdbCorrectionMap = "movie:douban:26862290=tmdb:65942";
            ReplacePluginConfiguration(configuration);
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("26862290", "Re：从零开始的休息时间", 2016, overview: "豆瓣简介");
            SeedDoubanSubject(doubanApi, subject);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovie(tmdbApi, 65942, "zh-CN", CreateTmdbMovie(65942, "Re：从零开始的异世界生活", 2016, overview: "主线简介"));
            var infoPath = "/mnt/media/Movies/Re：从零开始的异世界生活/Re：从零开始的异世界生活.mkv";
            var currentMovie = new TrackingMovie
            {
                Id = Guid.NewGuid(),
                Name = "Re：从零开始的休息时间",
                Overview = "旧豆瓣简介",
                Path = infoPath,
                ProviderIds = new Dictionary<string, string>
                {
                    [BaseProvider.DoubanProviderId] = "26862290",
                    [MetaSharkPlugin.ProviderId] = "Douban_26862290",
                    [MetadataProvider.Tmdb.ToString()] = "65942",
                },
            };
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.FindByPath(infoPath, false)).Returns(currentMovie);
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateExplicitRefreshContextAccessor(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)),
                libraryManager: libraryManager.Object,
                doubanApi: doubanApi,
                tmdbApi: tmdbApi);
            var info = CreateMovieInfo("Re：从零开始的休息时间", infoPath, 2016);
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "26862290",
                [MetaSharkPlugin.ProviderId] = "Douban_26862290",
                [MetadataProvider.Tmdb.ToString()] = "65942",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("Re：从零开始的休息时间", result.Item!.Name);
            Assert.AreEqual("豆瓣简介", result.Item.Overview);
            Assert.AreEqual("65942", result.Item.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("Douban_26862290", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual("26862290", result.Item.GetProviderId(BaseProvider.DoubanProviderId));
        }

        [TestMethod]
        public async Task GetMetadata_LlmDoubanCorrectionMapWithDefaultScraperMode_UsesTmdbMetadataAndDoesNotRequeryCorrection()
        {
            var configuration = CreateLlmConfiguration(enableTmdbCorrection: true);
            configuration.LlmTmdbCorrectionMap = "movie:douban:26862290=tmdb:65942";
            ReplacePluginConfiguration(configuration);
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("26862290", "Re：从零开始的休息时间", 2016, overview: "豆瓣简介");
            SeedDoubanSubject(doubanApi, subject);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovie(tmdbApi, 65942, "zh-CN", CreateTmdbMovie(65942, "Re：从零开始的异世界生活", 2016, overview: "主线简介"));
            var infoPath = "/mnt/media/Movies/Re：从零开始的异世界生活/Re：从零开始的异世界生活.mkv";
            var currentMovie = new TrackingMovie
            {
                Id = Guid.NewGuid(),
                Name = "Re：从零开始的休息时间",
                Overview = "旧豆瓣简介",
                Path = infoPath,
                ProviderIds = new Dictionary<string, string>
                {
                    [BaseProvider.DoubanProviderId] = "26862290",
                    [MetaSharkPlugin.ProviderId] = "Douban_26862290",
                    [MetadataProvider.Tmdb.ToString()] = "65942",
                },
            };
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.FindByPath(infoPath, false)).Returns(currentMovie);
            var externalIdResolver = new LlmProviderFlowTestHelpers.RecordingLlmExternalIdResolutionService();
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateExplicitRefreshContextAccessor(currentMovie.Id.ToString("D", CultureInfo.InvariantCulture)),
                libraryManager: libraryManager.Object,
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdResolver);
            var info = CreateMovieInfo("Re：从零开始的休息时间", infoPath, 2016);
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "26862290",
                [MetaSharkPlugin.ProviderId] = "Douban_26862290",
                [MetadataProvider.Tmdb.ToString()] = "65942",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, externalIdResolver.ExistingProviderDecisionRequests.Count, "配置里已有 Douban -> TMDb 纠错映射时，不应再次评估 LLM 纠错入口。");
            Assert.AreEqual(0, externalIdResolver.CorrectionRequests.Count, "配置里已有 Douban -> TMDb 纠错映射时，不应再次调用 LLM 纠错。");
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("Re：从零开始的异世界生活", result.Item!.Name);
            Assert.AreEqual("主线简介", result.Item.Overview);
            Assert.AreEqual("65942", result.Item.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("Tmdb_65942", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId));
        }

        [TestMethod]
        public async Task GetMetadata_TmdbCorrectionPersistenceFailure_StillUsesVerifiedTmdbMetadataForCurrentRefresh()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration(enableTmdbCorrection: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("verified-replacement-douban", "Wrong Douban Movie", 2045, overview: "豆瓣简介");
            SeedDoubanSubject(doubanApi, subject);
            var tmdbApi = new TmdbApi(loggerFactory);
            var tmdbMovie = CreateTmdbMovie(222, "Correct TMDb Movie", 2045, overview: "Correct TMDb overview");
            tmdbMovie.ImdbId = "tt222";
            SeedTmdbMovie(tmdbApi, 222, "zh-CN", tmdbMovie);
            var externalIdResolver = new LlmProviderFlowTestHelpers.RecordingLlmExternalIdResolutionService();
            externalIdResolver.EnqueueExistingProviderDecision(LlmAssistTriggerDecision.Allowed("StaleExternalIdConflict"));
            externalIdResolver.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.Verified("222", "test verified replacement"));
            var persistenceService = new Mock<ILlmTmdbCorrectionMapPersistenceService>();
            persistenceService
                .Setup(x => x.TryUpsertDoubanCorrectionAsync(nameof(Movie), "verified-replacement-douban", "222", It.IsAny<CancellationToken>()))
                .ReturnsAsync(LlmTmdbCorrectionMapPersistenceResult.Failed("SaveConfigurationFailed", string.Empty, string.Empty, null));
            var infoPath = "/mnt/media/Movies/Correct TMDb Movie/Correct TMDb Movie.mkv";
            var currentMovie = new TrackingMovie
            {
                Id = Guid.NewGuid(),
                Name = "Wrong Douban Movie",
                Overview = "旧豆瓣简介",
                Path = infoPath,
                ProviderIds = new Dictionary<string, string>
                {
                    [BaseProvider.DoubanProviderId] = "verified-replacement-douban",
                    [MetaSharkPlugin.ProviderId] = "Douban_verified-replacement-douban",
                    [MetadataProvider.Tmdb.ToString()] = "111",
                    [MetadataProvider.Imdb.ToString()] = "ttold",
                },
            };
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.FindByPath(infoPath, false)).Returns(currentMovie);
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateExplicitRefreshContextAccessor(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)),
                libraryManager: libraryManager.Object,
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdResolver,
                llmTmdbCorrectionMapPersistenceService: persistenceService.Object);
            var info = CreateMovieInfo("Wrong Douban Movie", infoPath, 2045);
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "verified-replacement-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_verified-replacement-douban",
                [MetadataProvider.Tmdb.ToString()] = "111",
                [MetadataProvider.Imdb.ToString()] = "ttold",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdResolver.ExistingProviderDecisionRequests.Count);
            Assert.AreEqual(1, externalIdResolver.CorrectionRequests.Count);
            persistenceService.Verify(x => x.TryUpsertDoubanCorrectionAsync(nameof(Movie), "verified-replacement-douban", "222", It.IsAny<CancellationToken>()), Times.Once);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("Correct TMDb Movie", result.Item!.Name);
            Assert.AreEqual("Correct TMDb overview", result.Item.Overview);
            Assert.AreEqual("222", result.Item.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("Tmdb_222", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId));
        }

        [TestMethod]
        public async Task GetMetadata_WhenManualRefreshCorrectionVerified_UsesTmdbMetadataAndClearsStaleDouban()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration(enableTmdbCorrection: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("manual-correction-douban", "Manual Correction Movie", 2037);
            SeedDoubanSubject(doubanApi, subject);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovie(tmdbApi, 222, "zh-CN", CreateTmdbMovie(222, "Corrected Manual Movie", 2037));
            var externalIdResolver = new LlmProviderFlowTestHelpers.RecordingLlmExternalIdResolutionService();
            externalIdResolver.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.Verified("222", "test verified replacement"));
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateExplicitRefreshContextAccessor(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdResolver);
            var info = CreateMovieInfo("Manual Correction Movie", "/mnt/media/Movies/Manual Correction Movie/Manual Correction Movie.mkv", 2037);
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "manual-correction-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_manual-correction-douban",
                [MetadataProvider.Tmdb.ToString()] = "111",
                [MetadataProvider.Imdb.ToString()] = "ttoldimdb",
                [MetadataProvider.Tvdb.ToString()] = "81001",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdResolver.ExistingProviderDecisionRequests.Count);
            Assert.AreEqual(1, externalIdResolver.CorrectionRequests.Count);
            Assert.AreEqual(0, externalIdResolver.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("Corrected Manual Movie", result.Item!.Name);
            Assert.AreEqual("Corrected Manual Movie overview", result.Item.Overview);
            Assert.AreEqual("222", result.Item!.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("ttoldimdb", result.Item.GetProviderId(MetadataProvider.Imdb));
            Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Tmdb_222", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual("81001", result.Item.GetProviderId(MetadataProvider.Tvdb));
            Assert.AreEqual("111", externalIdResolver.CorrectionRequests[0].OldTmdbId);
            Assert.AreEqual(DefaultScraperSemantic.UserRefresh, externalIdResolver.CorrectionRequests[0].Semantic);
        }

        [TestMethod]
        public async Task GetMetadata_WhenExistingTmdbVerified_UsesTmdbMetadataAndPersistsProviderIdCleanup()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration(enableTmdbCorrection: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var staleSubject = CreateDoubanSubject("verified-existing-douban", "Verified Existing Stale Movie", 1999);
            SeedDoubanSubject(doubanApi, staleSubject);
            var tmdbApi = new TmdbApi(loggerFactory);
            var tmdbMovie = CreateTmdbMovie(333, "Verified Existing TMDb Movie", 2044, overview: "Verified existing TMDb overview");
            tmdbMovie.ImdbId = "tt333";
            SeedTmdbMovie(tmdbApi, 333, "zh-CN", tmdbMovie);
            var externalIdResolver = new LlmProviderFlowTestHelpers.RecordingLlmExternalIdResolutionService();
            externalIdResolver.EnqueueExistingProviderDecision(LlmAssistTriggerDecision.Allowed("ExplicitSearchMissingMetadataRefresh"));
            externalIdResolver.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.VerifiedExistingTmdb("333", "ExistingTmdbVerified"));
            var infoPath = "/mnt/media/Movies/Verified Existing TMDb Movie/Verified Existing TMDb Movie.mkv";
            var currentMovie = new TrackingMovie
            {
                Id = Guid.NewGuid(),
                Name = "Verified Existing Stale Movie",
                Overview = "Old Douban overview",
                Path = infoPath,
                ProviderIds = new Dictionary<string, string>
                {
                    [BaseProvider.DoubanProviderId] = "verified-existing-douban",
                    [MetaSharkPlugin.ProviderId] = "Douban_verified-existing-douban",
                    [MetadataProvider.Tmdb.ToString()] = "333",
                    [MetadataProvider.Imdb.ToString()] = "tt333",
                },
            };
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.FindByPath(infoPath, false)).Returns(currentMovie);
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateExplicitRefreshContextAccessor(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)),
                libraryManager: libraryManager.Object,
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdResolver);
            var info = CreateMovieInfo("Verified Existing Stale Movie", infoPath, 2044);
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "verified-existing-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_verified-existing-douban",
                [MetadataProvider.Tmdb.ToString()] = "333",
                [MetadataProvider.Imdb.ToString()] = "tt333",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdResolver.ExistingProviderDecisionRequests.Count);
            Assert.AreEqual(1, externalIdResolver.CorrectionRequests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("Verified Existing TMDb Movie", result.Item!.Name);
            Assert.AreEqual("Verified existing TMDb overview", result.Item.Overview);
            Assert.AreEqual("333", result.Item.GetProviderId(MetadataProvider.Tmdb));
            Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Tmdb_333", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual("Verified Existing TMDb Movie", currentMovie.Name);
            Assert.AreEqual("Verified existing TMDb overview", currentMovie.Overview);
            Assert.IsFalse(currentMovie.ProviderIds.ContainsKey(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Tmdb_333", currentMovie.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual(1, currentMovie.UpdateToRepositoryCallCount);
            Assert.AreEqual(ItemUpdateType.MetadataEdit, currentMovie.LastUpdateReason);
        }

        [TestMethod]
        public async Task GetMetadata_WhenVerifiedReplacementUsesTmdbMetadataAndClearsStaleDouban()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration(enableTmdbCorrection: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var staleSubject = CreateDoubanSubject("verified-replacement-douban", "Wrong Douban Movie", 2045);
            SeedDoubanSubject(doubanApi, staleSubject);
            var tmdbApi = new TmdbApi(loggerFactory);
            var tmdbMovie = CreateTmdbMovie(222, "Correct TMDb Movie", 2045, overview: "Correct TMDb overview");
            tmdbMovie.ImdbId = "tt222";
            SeedTmdbMovie(tmdbApi, 222, "zh-CN", tmdbMovie);
            var externalIdResolver = new LlmProviderFlowTestHelpers.RecordingLlmExternalIdResolutionService();
            externalIdResolver.EnqueueExistingProviderDecision(LlmAssistTriggerDecision.Allowed("StaleExternalIdConflict"));
            externalIdResolver.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.Verified("222", "test verified replacement"));
            var infoPath = "/mnt/media/Movies/Correct TMDb Movie/Correct TMDb Movie.mkv";
            var currentMovie = new TrackingMovie
            {
                Id = Guid.NewGuid(),
                Name = "Wrong Douban Movie",
                Path = infoPath,
                ProviderIds = new Dictionary<string, string>
                {
                    [BaseProvider.DoubanProviderId] = "verified-replacement-douban",
                    [MetaSharkPlugin.ProviderId] = "Douban_verified-replacement-douban",
                    [MetadataProvider.Tmdb.ToString()] = "111",
                    [MetadataProvider.Imdb.ToString()] = "ttold",
                },
            };
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.FindByPath(infoPath, false)).Returns(currentMovie);
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateExplicitRefreshContextAccessor(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)),
                libraryManager: libraryManager.Object,
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdResolver);
            var info = CreateMovieInfo("Wrong Douban Movie", infoPath, 2045);
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "verified-replacement-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_verified-replacement-douban",
                [MetadataProvider.Tmdb.ToString()] = "111",
                [MetadataProvider.Imdb.ToString()] = "ttold",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdResolver.ExistingProviderDecisionRequests.Count);
            Assert.AreEqual(1, externalIdResolver.CorrectionRequests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("Correct TMDb Movie", result.Item!.Name);
            Assert.AreEqual("Correct TMDb overview", result.Item.Overview);
            Assert.AreEqual("222", info.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("222", result.Item.GetProviderId(MetadataProvider.Tmdb));
            Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Tmdb_222", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual("ttold", result.Item.GetProviderId(MetadataProvider.Imdb));
            Assert.IsFalse(currentMovie.ProviderIds.ContainsKey(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Tmdb_222", currentMovie.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual("222", currentMovie.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual(1, currentMovie.UpdateToRepositoryCallCount);
            Assert.AreEqual(ItemUpdateType.MetadataEdit, currentMovie.LastUpdateReason);
        }

        [TestMethod]
        public async Task GetMetadata_WhenManualIdentifyCorrectionVerified_UsesTmdbMetadataAndClearsStaleDouban()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration(enableTmdbCorrection: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("manual-identify-correction-douban", "Manual Identify Correction Movie", 2040);
            SeedDoubanSubject(doubanApi, subject);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovie(tmdbApi, 222, "zh-CN", CreateTmdbMovie(222, "Manual Identify Corrected Movie", 2040));
            var externalIdResolver = new LlmProviderFlowTestHelpers.RecordingLlmExternalIdResolutionService();
            externalIdResolver.EnqueueExistingProviderDecision(LlmAssistTriggerDecision.Allowed("StaleExternalIdConflict"));
            externalIdResolver.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.Verified("222", "test verified replacement"));
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdResolver);
            var info = CreateMovieInfo("Manual Identify Correction Movie", "/mnt/media/Movies/Manual Identify Correction Movie/Manual Identify Correction Movie.mkv", 2040);
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "manual-identify-correction-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_manual-identify-correction-douban",
                [MetadataProvider.Tmdb.ToString()] = "111",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdResolver.CorrectionRequests.Count);
            Assert.AreEqual(DefaultScraperSemantic.ManualMatch, externalIdResolver.CorrectionRequests[0].Semantic);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("Manual Identify Corrected Movie", result.Item!.Name);
            Assert.AreEqual("Manual Identify Corrected Movie overview", result.Item.Overview);
            Assert.AreEqual("222", result.Item!.GetProviderId(MetadataProvider.Tmdb));
            Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Tmdb_222", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
        }

        [TestMethod]
        public async Task GetMetadata_WhenExplicitSearchMissingCorrectionVerified_UsesTmdbMetadataAndClearsStaleDouban()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration(enableTmdbCorrection: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("search-missing-correction-douban", "Search Missing Correction Movie", 2041);
            SeedDoubanSubject(doubanApi, subject);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovie(tmdbApi, 222, "zh-CN", CreateTmdbMovie(222, "Search Missing Corrected Movie", 2041));
            var externalIdResolver = new LlmProviderFlowTestHelpers.RecordingLlmExternalIdResolutionService();
            externalIdResolver.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.Verified("222", "test verified replacement"));
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateExplicitSearchMissingContextAccessor(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdResolver);
            var info = CreateMovieInfo("Search Missing Correction Movie", "/mnt/media/Movies/Search Missing Correction Movie/Search Missing Correction Movie.mkv", 2041);
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "search-missing-correction-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_search-missing-correction-douban",
                [MetadataProvider.Tmdb.ToString()] = "111",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdResolver.ExistingProviderDecisionRequests.Count);
            Assert.AreEqual(1, externalIdResolver.CorrectionRequests.Count);
            Assert.AreEqual(DefaultScraperSemantic.UserRefresh, externalIdResolver.CorrectionRequests[0].Semantic);
            Assert.IsFalse(externalIdResolver.CorrectionRequests[0].HttpContext!.Request.QueryString.Value!.Contains("replaceAllMetadata=true", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("Search Missing Corrected Movie", result.Item!.Name);
            Assert.AreEqual("Search Missing Corrected Movie overview", result.Item.Overview);
            Assert.AreEqual("222", result.Item!.GetProviderId(MetadataProvider.Tmdb));
            Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Tmdb_222", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
        }

        [TestMethod]
        public async Task GetMetadata_TmdbCorrection_WhenCorrectionReturnsNoReplacement_PreservesExistingTmdb()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration(enableTmdbCorrection: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("weak-evidence-douban", "Weak Evidence Movie", 2042);
            SeedDoubanSubject(doubanApi, subject);
            var externalIdResolver = new LlmProviderFlowTestHelpers.RecordingLlmExternalIdResolutionService();
            externalIdResolver.EnqueueExistingProviderDecision(LlmAssistTriggerDecision.Allowed("StaleExternalIdConflict"));
            externalIdResolver.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.NoReplacement("StrongEvidenceMissing"));
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                llmExternalIdResolutionService: externalIdResolver);
            var info = CreateMovieInfo("Weak Evidence Movie", "/mnt/media/Movies/Weak Evidence Movie/Weak Evidence Movie.mkv", 2042);
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "weak-evidence-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_weak-evidence-douban",
                [MetadataProvider.Tmdb.ToString()] = "111",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdResolver.ExistingProviderDecisionRequests.Count);
            Assert.AreEqual(1, externalIdResolver.CorrectionRequests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("111", info.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("111", result.Item!.GetProviderId(MetadataProvider.Tmdb));
        }

        [TestMethod]
        public async Task GetMetadata_TmdbCorrection_BridgedSearchMissingRefreshAndStaleConflict_StillInvokesCorrectionEvaluation()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration(enableTmdbCorrection: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("whitelisted-stale-douban", "Corrected Whitelisted Movie", 2046);
            SeedDoubanSubject(doubanApi, subject);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovie(tmdbApi, 222, "zh-CN", CreateTmdbMovie(222, "Corrected Whitelisted Movie", 2046));
            var externalIdResolver = new LlmProviderFlowTestHelpers.RecordingLlmExternalIdResolutionService();
            externalIdResolver.EnqueueExistingProviderDecision(LlmAssistTriggerDecision.Allowed("StaleExternalIdConflict"));
            externalIdResolver.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.Verified("222", "test whitelisted stale replacement"));
            var refreshIntentStore = new InMemoryTmdbCorrectionRefreshIntentStore();
            var itemId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
            var refreshContext = LlmProviderFlowTestHelpers.CreateExplicitSearchMissingHttpContext(itemId);
            Assert.IsTrue(TmdbCorrectionRefreshIntentClassifier.TryResolveExplicitSearchMissingMetadataRefreshItemId(refreshContext, out var refreshItemId));
            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = refreshContext,
            };
            refreshIntentStore.Save(refreshItemId, "/mnt/media/Movies/Main Story (2046)/特典/第01集.mkv");
            httpContextAccessor.HttpContext!.Request.QueryString = QueryString.Empty;
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: httpContextAccessor,
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdResolver,
                tmdbCorrectionRefreshIntentStore: refreshIntentStore);
            var info = CreateMovieInfo("Wrong Spinoff Movie", "/mnt/media/Movies/Main Story (2046)/特典/第01集.mkv", 2046);
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "whitelisted-stale-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_whitelisted-stale-douban",
                [MetadataProvider.Tmdb.ToString()] = "111",
                [MetadataProvider.Imdb.ToString()] = "ttspinoff111",
                [MetadataProvider.Tvdb.ToString()] = "tvdb-spinoff-111",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdResolver.ExistingProviderDecisionRequests.Count);
            Assert.AreEqual(DefaultScraperSemantic.UserRefresh, externalIdResolver.ExistingProviderDecisionRequests[0].Semantic);
            Assert.AreEqual(1, externalIdResolver.CorrectionRequests.Count, "bridge search-missing refresh + 明显冲突时应进入 correction evaluation。 ");
            Assert.AreEqual(DefaultScraperSemantic.UserRefresh, externalIdResolver.CorrectionRequests[0].Semantic);
            Assert.AreEqual(QueryString.Empty.Value, externalIdResolver.CorrectionRequests[0].HttpContext!.Request.QueryString.Value);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("222", info.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("222", result.Item!.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("ttspinoff111", result.Item.GetProviderId(MetadataProvider.Imdb));
            Assert.AreEqual("tvdb-spinoff-111", result.Item.GetProviderId(MetadataProvider.Tvdb));
            Assert.IsTrue(refreshIntentStore.HasPending(refreshItemId, info.Path), "桥接 search-missing intent 需要保留到短 TTL 窗口，供 Jellyfin 同轮后续 queryless provider 调用复用。 ");
        }

        [TestMethod]
        public async Task GetMetadata_TmdbCorrection_EmptyRefreshWithoutBridge_DoesNotInvokeCorrectionEvaluation()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration(enableTmdbCorrection: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("no-bridge-stale-douban", "No Bridge Movie", 2047);
            SeedDoubanSubject(doubanApi, subject);
            var externalIdResolver = new LlmProviderFlowTestHelpers.RecordingLlmExternalIdResolutionService();
            externalIdResolver.EnqueueExistingProviderDecision(LlmAssistTriggerDecision.Allowed("StaleExternalIdConflict"));
            externalIdResolver.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.Verified("222", "must not be consumed without bridge"));
            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = LlmProviderFlowTestHelpers.CreateExplicitRefreshHttpContext(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture), metadataRefreshMode: "RefreshMetadata", replaceAllMetadata: false),
            };
            httpContextAccessor.HttpContext!.Request.QueryString = QueryString.Empty;
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: httpContextAccessor,
                doubanApi: doubanApi,
                llmExternalIdResolutionService: externalIdResolver,
                tmdbCorrectionRefreshIntentStore: new InMemoryTmdbCorrectionRefreshIntentStore());
            var info = CreateMovieInfo("No Bridge Movie", "/mnt/media/Movies/No Bridge Movie/No Bridge Movie.mkv", 2047);
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "no-bridge-stale-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_no-bridge-stale-douban",
                [MetadataProvider.Tmdb.ToString()] = "111",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, externalIdResolver.CorrectionRequests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("111", result.Item!.GetProviderId(MetadataProvider.Tmdb));
        }

        [TestMethod]
        public async Task GetMetadata_WhenExistingTmdbAndNoHttpContext_DoesNotCallCorrection()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration(enableTmdbCorrection: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("no-context-correction-douban", "No Context Correction Movie", 2043);
            SeedDoubanSubject(doubanApi, subject);
            var externalIdResolver = new LlmProviderFlowTestHelpers.RecordingLlmExternalIdResolutionService();
            externalIdResolver.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.Verified("222", "test verified replacement"));
            var provider = CreateProvider(
                loggerFactory,
                doubanApi: doubanApi,
                llmExternalIdResolutionService: externalIdResolver);
            var info = CreateMovieInfo("No Context Correction Movie", "/mnt/media/Movies/No Context Correction Movie/No Context Correction Movie.mkv", 2043);
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "no-context-correction-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_no-context-correction-douban",
                [MetadataProvider.Tmdb.ToString()] = "111",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, externalIdResolver.CorrectionRequests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("111", result.Item!.GetProviderId(MetadataProvider.Tmdb));
        }

        [TestMethod]
        public async Task GetMetadata_WhenGlobalLlmDisabled_DoesNotCallCorrection()
        {
            ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableLlmAssist = false,
                EnableLlmTmdbIdCorrection = true,
                LlmBaseUrl = "http://127.0.0.1:11434/v1",
                LlmModel = "test-model",
                LlmApiKey = "sk-test-secret",
            });
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("llm-disabled-correction-douban", "LLM Disabled Correction Movie", 2044);
            SeedDoubanSubject(doubanApi, subject);
            var externalIdResolver = new LlmProviderFlowTestHelpers.RecordingLlmExternalIdResolutionService();
            externalIdResolver.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.Verified("222", "test verified replacement"));
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                llmExternalIdResolutionService: externalIdResolver);
            var info = CreateMovieInfo("LLM Disabled Correction Movie", "/mnt/media/Movies/LLM Disabled Correction Movie/LLM Disabled Correction Movie.mkv", 2044);
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "llm-disabled-correction-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_llm-disabled-correction-douban",
                [MetadataProvider.Tmdb.ToString()] = "111",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, externalIdResolver.CorrectionRequests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("111", result.Item!.GetProviderId(MetadataProvider.Tmdb));
        }

        [TestMethod]
        public async Task GetMetadata_WhenCorrectionPathContextDisabled_SendsPublicIdsWithoutPath()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration(enableTmdbCorrection: true));
            MetaSharkPlugin.Instance!.Configuration.LlmAllowRelativePathContext = false;
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("privacy-correction-douban", "Privacy Correction Movie", 2045);
            SeedDoubanSubject(doubanApi, subject);
            var externalIdResolver = new LlmProviderFlowTestHelpers.RecordingLlmExternalIdResolutionService();
            externalIdResolver.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.NoReplacement("StrongEvidenceMissing"));
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                llmExternalIdResolutionService: externalIdResolver);
            var info = CreateMovieInfo("Privacy Correction Movie", "/mnt/media/Movies/Private Folder/Privacy Correction Movie/Secret.File.mkv", 2045);
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "privacy-correction-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_privacy-correction-douban",
                [MetadataProvider.Tmdb.ToString()] = "111",
                [MetadataProvider.Imdb.ToString()] = "tt2045001",
                [MetadataProvider.Tvdb.ToString()] = "204500",
                ["apiKey"] = "sk-test-secret",
            };

            _ = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdResolver.CorrectionRequests.Count);
            var lookup = (MovieInfo)externalIdResolver.CorrectionRequests[0].LookupInfo!;
            Assert.AreEqual(string.Empty, lookup.Path);
            Assert.AreEqual("111", lookup.ProviderIds![MetadataProvider.Tmdb.ToString()]);
            Assert.AreEqual("tt2045001", lookup.ProviderIds[MetadataProvider.Imdb.ToString()]);
            Assert.AreEqual("204500", lookup.ProviderIds[MetadataProvider.Tvdb.ToString()]);
            Assert.AreEqual("privacy-correction-douban", lookup.ProviderIds[BaseProvider.DoubanProviderId]);
            Assert.IsFalse(lookup.ProviderIds.ContainsKey(MetaSharkPlugin.ProviderId));
            Assert.IsFalse(lookup.ProviderIds.ContainsKey("apiKey"));
            LlmProviderFlowTestHelpers.AssertNoSensitiveContent(lookup.Path, externalIdResolver.CorrectionRequests[0].Name);
        }

        [TestMethod]
        public async Task GetMetadata_WhenOverwriteRefreshCorrectionVerified_DoesNotCallOrdinaryLlmPaths()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration(enableTmdbCorrection: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            SeedDoubanSearchResults(doubanApi, "Overwrite Corrected Movie", Array.Empty<DoubanSubject>());
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovie(tmdbApi, 222, "zh-CN", CreateTmdbMovie(222, "Overwrite Corrected Movie", 2038));
            var externalIdResolver = new LlmProviderFlowTestHelpers.RecordingLlmExternalIdResolutionService();
            externalIdResolver.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.Verified("222", "test verified replacement"));
            externalIdResolver.EnqueueResult(LlmExternalIdResolutionResult.Succeeded(
                Array.Empty<LlmExternalIdCandidate>(),
                new[] { CreateProviderIdWrite(MetadataProvider.Tmdb.ToString(), "TMDb", "999") },
                Array.Empty<LlmExternalIdProviderIdWrite>(),
                "ordinary resolver should not run"));
            var textLlm = CreateSuccessfulLlm("Ignored Overwrite Movie", 2038);
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateExplicitRefreshContextAccessor(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture), replaceAllMetadata: true),
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmMetadataAssistService: textLlm,
                llmExternalIdResolutionService: externalIdResolver);
            var info = CreateMovieInfo("Overwrite Corrected Movie", "/mnt/media/Movies/Overwrite Corrected Movie/Overwrite Corrected Movie.mkv", 2038);
            info.ProviderIds = new Dictionary<string, string>
            {
                [MetaSharkPlugin.ProviderId] = "Tmdb_111",
                [MetadataProvider.Tmdb.ToString()] = "111",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdResolver.CorrectionRequests.Count);
            Assert.AreEqual(0, externalIdResolver.Requests.Count);
            Assert.AreEqual(0, textLlm.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("222", result.Item!.GetProviderId(MetadataProvider.Tmdb));
        }

        [TestMethod]
        public async Task GetMetadata_WhenAutomaticRefreshWithExistingTmdb_DoesNotCallCorrection()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration(enableTmdbCorrection: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbMovie(tmdbApi, 111, "zh-CN", CreateTmdbMovie(111, "Automatic Correction Rejected Movie", 2039));
            var externalIdResolver = new LlmProviderFlowTestHelpers.RecordingLlmExternalIdResolutionService();
            externalIdResolver.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.Verified("222", "test verified replacement"));
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateAutomaticRefreshContextAccessor(),
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdResolver);
            var info = CreateMovieInfo("Automatic Correction Rejected Movie", "/mnt/media/Movies/Automatic Correction Rejected Movie/Automatic Correction Rejected Movie.mkv", 2039);
            info.IsAutomated = true;
            info.ProviderIds = new Dictionary<string, string>
            {
                [MetaSharkPlugin.ProviderId] = "Tmdb_111",
                [MetadataProvider.Tmdb.ToString()] = "111",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, externalIdResolver.CorrectionRequests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("111", result.Item!.GetProviderId(MetadataProvider.Tmdb));
        }

        [TestMethod]
        public async Task GetMetadata_DoesNotCallExternalIdResolver_WhenAutomaticRefresh()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("automatic-id-douban", "Automatic ID Movie", 2033);
            SeedDoubanSubject(doubanApi, subject);
            var externalIdResolver = CreateExternalIdResolverWithWrites(CreateProviderIdWrite(MetadataProvider.Tmdb.ToString(), "TMDb", "6401"));
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateAutomaticRefreshContextAccessor(),
                doubanApi: doubanApi,
                llmExternalIdResolutionService: externalIdResolver);
            var info = CreateMovieInfo("Automatic ID Movie", "/mnt/media/Movies/Automatic ID Movie/Automatic ID Movie.mkv", 2033);
            info.IsAutomated = true;
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "automatic-id-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_automatic-id-douban",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, externalIdResolver.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.IsNull(result.Item!.GetProviderId(MetadataProvider.Tmdb));
        }

        [TestMethod]
        public async Task GetMetadata_DoesNotCallExternalIdResolver_WhenUserRefreshHasNoHttpContext()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("null-http-id-douban", "Null Http ID Movie", 2035);
            SeedDoubanSubject(doubanApi, subject);
            var externalIdResolver = CreateExternalIdResolverWithWrites(CreateProviderIdWrite(MetadataProvider.Tmdb.ToString(), "TMDb", "6601"));
            var provider = CreateProvider(
                loggerFactory,
                doubanApi: doubanApi,
                llmExternalIdResolutionService: externalIdResolver);
            var info = CreateMovieInfo("Null Http ID Movie", "/mnt/media/Movies/Null Http ID Movie/Null Http ID Movie.mkv", 2035);
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "null-http-id-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_null-http-id-douban",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, externalIdResolver.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("null-http-id-douban", result.Item!.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.IsNull(result.Item.GetProviderId(MetadataProvider.Tmdb));
        }

        [TestMethod]
        public async Task GetMetadata_DoesNotCallExternalIdResolver_WhenOverwriteRefresh()
        {
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var subject = CreateDoubanSubject("overwrite-id-douban", "Overwrite ID Movie", 2034);
            SeedDoubanSubject(doubanApi, subject);
            var externalIdResolver = CreateExternalIdResolverWithWrites(CreateProviderIdWrite(MetadataProvider.Tmdb.ToString(), "TMDb", "6501"));
            var itemId = Guid.NewGuid();
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateExplicitRefreshContextAccessor(itemId.ToString("N", CultureInfo.InvariantCulture), replaceAllMetadata: true),
                doubanApi: doubanApi,
                llmExternalIdResolutionService: externalIdResolver);
            var info = CreateMovieInfo("Overwrite ID Movie", "/mnt/media/Movies/Overwrite ID Movie/Overwrite ID Movie.mkv", 2034);
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "overwrite-id-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_overwrite-id-douban",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, externalIdResolver.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.IsNull(result.Item!.GetProviderId(MetadataProvider.Tmdb));
        }

        private static MovieProvider CreateProvider(
            ILoggerFactory loggerFactory,
            IHttpContextAccessor? httpContextAccessor = null,
            ILibraryManager? libraryManager = null,
            DoubanApi? doubanApi = null,
            TmdbApi? tmdbApi = null,
            ILlmMetadataAssistService? llmMetadataAssistService = null,
            ILlmExternalIdResolutionService? llmExternalIdResolutionService = null,
            ITmdbCorrectionRefreshIntentStore? tmdbCorrectionRefreshIntentStore = null,
            ILlmTmdbCorrectionMapPersistenceService? llmTmdbCorrectionMapPersistenceService = null)
        {
            var defaultLibraryManager = new Mock<ILibraryManager>();
            return new MovieProvider(
                new DefaultHttpClientFactory(),
                loggerFactory,
                libraryManager ?? defaultLibraryManager.Object,
                httpContextAccessor ?? new HttpContextAccessor { HttpContext = null },
                doubanApi ?? new DoubanApi(loggerFactory),
                tmdbApi ?? new TmdbApi(loggerFactory),
                new OmdbApi(loggerFactory),
                new ImdbApi(loggerFactory),
                new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore(),
                llmMetadataAssistService,
                llmExternalIdResolutionService,
                tmdbCorrectionRefreshIntentStore,
                llmTmdbCorrectionMapPersistenceService);
        }

        private static MovieInfo CreateMovieInfo(string name, string path, int? year)
        {
            return new MovieInfo
            {
                Name = name,
                Path = path,
                Year = year,
                MetadataLanguage = "zh-CN",
                IsAutomated = false,
            };
        }

        private static PluginConfiguration CreateLlmConfiguration(bool allowTextCompletion = true, bool enableTmdbCorrection = false)
        {
            return new PluginConfiguration
            {
                EnableLlmAssist = true,
                EnableLlmTmdbIdCorrection = enableTmdbCorrection,
                LlmBaseUrl = "http://127.0.0.1:11434/v1",
                LlmModel = "test-model",
                LlmApiKey = "sk-test-secret",
                LlmAllowTextCompletion = allowTextCompletion,
                LlmConfidenceThreshold = 0.75,
            };
        }

        private static LlmProviderFlowTestHelpers.RecordingLlmExternalIdResolutionService CreateExternalIdResolverWithWrites(params LlmExternalIdProviderIdWrite[] writes)
        {
            var resolver = new LlmProviderFlowTestHelpers.RecordingLlmExternalIdResolutionService();
            resolver.EnqueueResult(LlmExternalIdResolutionResult.Succeeded(
                writes.Select(write => write.Candidate).ToArray(),
                writes,
                Array.Empty<LlmExternalIdProviderIdWrite>(),
                "Succeeded"));
            return resolver;
        }

        private static LlmExternalIdProviderIdWrite CreateProviderIdWrite(string providerIdKey, string provider, string providerIdValue)
        {
            return new LlmExternalIdProviderIdWrite(
                providerIdKey,
                provider,
                providerIdValue,
                "Movie",
                CreateCandidate(provider, providerIdValue, "Movie"));
        }

        private static LlmExternalIdCandidate CreateCandidate(string provider, string id, string mediaType)
        {
            return new LlmExternalIdCandidate
            {
                Provider = provider,
                Id = id,
                MediaType = mediaType,
                Confidence = 0.95,
                Reason = "verified by test resolver",
                Evidence = "provider id write plan",
            };
        }

        private static LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService CreateSuccessfulLlm(string title, int? year, string? originalTitle = null, string? overview = null)
        {
            var llm = new LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService();
            var result = LlmScrapingAssistResult.Succeeded(
                new LlmPromptContext
                {
                    MediaType = "Movie",
                    RelativePath = $"Movies/{title}/{title}.mkv",
                    FileName = $"{title}.mkv",
                    ParsedName = title,
                    ParsedYear = year,
                },
                new LlmScrapingSuggestion
                {
                    MediaType = "Movie",
                    Title = title,
                    Year = year,
                    OriginalTitle = originalTitle,
                    Overview = overview,
                    Confidence = 0.95,
                },
                new LlmSearchHints
                {
                    Title = title,
                    Year = year,
                });
            llm.EnqueueResult(result);
            return llm;
        }

        private static DoubanSubject CreateDoubanSubject(string sid, string name, int year, string? overview = null)
        {
            return new DoubanSubject
            {
                Sid = sid,
                Name = name,
                OriginalName = name,
                Year = year,
                Category = "电影",
                Genre = "剧情",
                Rating = 8.1f,
                Intro = overview ?? $"{name} overview",
                Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p0000000000.webp",
            };
        }

        private static SearchMovie CreateSearchMovie(int tmdbId, string title, int year)
        {
            return new SearchMovie
            {
                Id = tmdbId,
                Title = title,
                OriginalTitle = title,
                ReleaseDate = new DateTime(year, 1, 1),
            };
        }

        private static TmdbMovie CreateTmdbMovie(int tmdbId, string title, int year, string? originalTitle = null, string? overview = null)
        {
            return new TmdbMovie
            {
                Id = tmdbId,
                Title = title,
                OriginalTitle = originalTitle ?? title,
                Overview = overview ?? $"{title} overview",
                ImdbId = string.Empty,
                ReleaseDate = new DateTime(year, 1, 1),
                VoteAverage = 7.8,
                ProductionCountries = new List<ProductionCountry>(),
                ProductionCompanies = new List<ProductionCompany>(),
                Genres = new List<TmdbGenre>(),
            };
        }

        private static void SeedDoubanSubject(DoubanApi doubanApi, DoubanSubject subject)
        {
            var cache = GetDoubanMemoryCache(doubanApi);
            cache.Set($"movie_{subject.Sid}", subject, TimeSpan.FromMinutes(5));
            cache.Set($"celebrities_{subject.Sid}", new List<DoubanCelebrity>(), TimeSpan.FromMinutes(5));
        }

        private static void SeedDoubanSearchResults(DoubanApi doubanApi, string keyword, params DoubanSubject[] subjects)
        {
            SeedDoubanSearchResults(doubanApi, keyword, (IEnumerable<DoubanSubject>)subjects);
        }

        private static void SeedDoubanSearchResults(DoubanApi doubanApi, string keyword, IEnumerable<DoubanSubject> subjects)
        {
            GetDoubanMemoryCache(doubanApi).Set($"search_{keyword}", subjects.ToList(), TimeSpan.FromMinutes(5));
        }

        private static void SeedTmdbMovie(TmdbApi tmdbApi, int tmdbId, string language, TmdbMovie movie)
        {
            GetTmdbMemoryCache(tmdbApi).Set($"movie-{tmdbId}-{language}-{language}", movie, TimeSpan.FromMinutes(5));
        }

        private static void SeedTmdbMovieSearchResults(TmdbApi tmdbApi, string name, int year, string language, params SearchMovie[] results)
        {
            GetTmdbMemoryCache(tmdbApi).Set(
                $"moviesearch-{name}-{year.ToString(CultureInfo.InvariantCulture)}-{language}",
                new SearchContainer<SearchMovie>
                {
                    Results = results.ToList(),
                },
                TimeSpan.FromMinutes(5));
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

            Assert.Fail("Could not replace MetaSharkPlugin configuration for tests.");
        }

        private sealed class TrackingMovie : Movie
        {
            public int MetadataChangedCallCount { get; private set; }

            public int UpdateToRepositoryCallCount { get; private set; }

            public ItemUpdateType? LastUpdateReason { get; private set; }

            public override ItemUpdateType OnMetadataChanged()
            {
                this.MetadataChangedCallCount++;
                return ItemUpdateType.MetadataEdit;
            }

            public override Task UpdateToRepositoryAsync(ItemUpdateType updateReason, CancellationToken cancellationToken)
            {
                this.UpdateToRepositoryCallCount++;
                this.LastUpdateReason = updateReason;
                return Task.CompletedTask;
            }
        }
    }
}
