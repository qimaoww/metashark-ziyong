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
                httpContextAccessor: LlmProviderFlowTestHelpers.CreateExplicitSearchMissingContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
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

        private static MovieProvider CreateProvider(
            ILoggerFactory loggerFactory,
            IHttpContextAccessor? httpContextAccessor = null,
            DoubanApi? doubanApi = null,
            TmdbApi? tmdbApi = null,
            ILlmMetadataAssistService? llmMetadataAssistService = null)
        {
            var libraryManager = new Mock<ILibraryManager>();
            return new MovieProvider(
                new DefaultHttpClientFactory(),
                loggerFactory,
                libraryManager.Object,
                httpContextAccessor ?? new HttpContextAccessor { HttpContext = null },
                doubanApi ?? new DoubanApi(loggerFactory),
                tmdbApi ?? new TmdbApi(loggerFactory),
                new OmdbApi(loggerFactory),
                new ImdbApi(loggerFactory),
                new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore(),
                llmMetadataAssistService);
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

        private static PluginConfiguration CreateLlmConfiguration(bool allowTextCompletion = false)
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

    }
}
