using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Providers.Llm;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [TestCategory("Stable")]
    [DoNotParallelize]
    public class SeasonProviderLlmAssistTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-season-provider-llm-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");

        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
            builder.AddSimpleConsole(options =>
            {
                options.IncludeScopes = true;
                options.SingleLine = true;
                options.TimestampFormat = "hh:mm:ss ";
            }));

        [TestInitialize]
        public void ResetConfigurationBeforeTest()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateBaseConfiguration());
        }

        [TestCleanup]
        public void ResetConfigurationAfterTest()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(new PluginConfiguration());
        }

        [TestMethod]
        public async Task GetMetadata_WhenLlmConfigDisabled_DoesNotCallLlm()
        {
            ReplacePluginConfiguration(CreateBaseConfiguration(enableLlmAssist: false));
            var llm = new LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService();
            var provider = this.CreateProvider(LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(), llm);

            var result = await provider.GetMetadata(CreateExternalMissingInfo(), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.HasMetadata);
            Assert.AreEqual(0, llm.Requests.Count);
        }

        [TestMethod]
        public async Task GetMetadata_WhenAutomaticRefresh_DoesNotCallLlm()
        {
            var llm = new LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService();
            var provider = this.CreateProvider(LlmProviderFlowTestHelpers.CreateAutomaticRefreshContextAccessor(), llm);
            var info = CreateExternalMissingInfo();
            info.IsAutomated = true;

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.HasMetadata);
            Assert.AreEqual(0, llm.Requests.Count);
        }

        [TestMethod]
        public async Task GetMetadata_WhenManualRefreshAndExternalSourcesMissing_UsesLlmTextOnly()
        {
            var llm = new LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService();
            llm.EnqueueResult(CreateSucceededLlmResult("LLM 季标题", "LLM 季简介", seasonNumber: 99));
            var provider = this.CreateProvider(LlmProviderFlowTestHelpers.CreateExplicitRefreshContextAccessor(Guid.NewGuid().ToString()), llm);

            var result = await provider.GetMetadata(CreateExternalMissingInfo(), CancellationToken.None).ConfigureAwait(false);

            AssertLlmTextResult(result, expectedIndexNumber: 2);
            Assert.AreEqual("LLM 季标题", result.Item!.Name);
            Assert.AreEqual("LLM 季简介", result.Item.Overview);
            AssertNoProviderIds(result.Item);
            Assert.AreEqual(1, llm.Requests.Count);
            AssertSeasonLlmRequestIsSafe(llm.Requests[0]);
        }

        [TestMethod]
        public async Task GetMetadata_WhenExplicitSearchMissingAndSafeFolderPath_UsesSanitizedLlmRequest()
        {
            var llm = new LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService();
            llm.EnqueueResult(CreateSucceededLlmResult("补全季标题", "补全季简介", seasonNumber: 2));
            var provider = this.CreateProvider(LlmProviderFlowTestHelpers.CreateExplicitSearchMissingContextAccessor(Guid.NewGuid().ToString()), llm);

            var result = await provider.GetMetadata(CreateExternalMissingInfo(), CancellationToken.None).ConfigureAwait(false);

            AssertLlmTextResult(result, expectedIndexNumber: 2);
            Assert.AreEqual(1, llm.Requests.Count);
            var lookupInfo = llm.Requests[0].LookupInfo;
            Assert.IsNotNull(lookupInfo);
            Assert.AreEqual("TV/示例剧/Season 02", lookupInfo!.Path);
            LlmProviderFlowTestHelpers.AssertNoSensitiveContent(llm.Requests[0], null, lookupInfo.Path);
        }

        [TestMethod]
        public async Task GetMetadata_WhenLlmFails_ReturnsExternalMissingFallback()
        {
            var llm = new LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService();
            llm.EnqueueResult(LlmScrapingAssistResult.Failed("LLM unavailable"));
            var provider = this.CreateProvider(LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(), llm);

            var result = await provider.GetMetadata(CreateExternalMissingInfo(), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.HasMetadata);
            Assert.IsNull(result.Item);
            Assert.AreEqual(1, llm.Requests.Count);
        }

        [TestMethod]
        public async Task GetMetadata_WhenLlmSuggestsDifferentSeasonNumber_DoesNotChangeIndexNumber()
        {
            var llm = new LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService();
            llm.EnqueueResult(CreateSucceededLlmResult("LLM 不改季号", "LLM 简介", seasonNumber: 4));
            var provider = this.CreateProvider(LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(), llm);

            var result = await provider.GetMetadata(CreateExternalMissingInfo(), CancellationToken.None).ConfigureAwait(false);

            AssertLlmTextResult(result, expectedIndexNumber: 2);
            Assert.AreEqual(2, llm.Requests[0].LookupInfo!.IndexNumber);
        }

        [TestMethod]
        public async Task GetMetadata_WhenSeasonNumberGuessedFromFolder_DoesNotLetLlmChangeGuess()
        {
            var llm = new LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService();
            llm.EnqueueResult(CreateSucceededLlmResult("猜测季标题", "猜测季简介", seasonNumber: 5));
            var provider = this.CreateProvider(LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(), llm);
            var info = CreateExternalMissingInfo(indexNumber: null);
            info.Path = "/mnt/media/TV/示例剧/S02";

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            AssertLlmTextResult(result, expectedIndexNumber: 2);
            Assert.IsNull(llm.Requests[0].LookupInfo!.IndexNumber);
        }

        [TestMethod]
        public async Task GetMetadata_WhenLlmTextCompletionDisabled_DoesNotCallLlmForPureGeneratedSeasonMetadata()
        {
            var configuration = CreateBaseConfiguration();
            configuration.LlmAllowTextCompletion = false;
            ReplacePluginConfiguration(configuration);
            var llm = new LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService();
            var provider = this.CreateProvider(LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(), llm);

            var result = await provider.GetMetadata(CreateExternalMissingInfo(), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.HasMetadata);
            Assert.AreEqual(0, llm.Requests.Count);
        }

        [TestMethod]
        public async Task GetMetadata_WhenDeterministicMismatchExists_ReturnsNoMetadataAndDoesNotChangeIdsOrSeasonNumber()
        {
            var llm = new LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService();
            llm.EnqueueResult(LlmScrapingAssistResult.Failed("SeasonEpisodeMismatch", new LlmPromptContext { MediaType = "Season", SeasonNumber = 2 }));
            var provider = this.CreateProvider(LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(), llm);
            var info = CreateExternalMissingInfo();
            var providerIdSnapshot = LlmProviderFlowTestHelpers.CloneProviderIds(info.ProviderIds);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.HasMetadata);
            Assert.IsNull(result.Item);
            Assert.AreEqual(2, info.IndexNumber);
            LlmProviderFlowTestHelpers.AssertProviderIdsUnchanged(providerIdSnapshot, info.ProviderIds);
            Assert.AreEqual(1, llm.Requests.Count);
        }

        [TestMethod]
        public async Task GetMetadata_WhenDoubanSeasonTitleExists_DoesNotCallOrOverwriteWithLlm()
        {
            var llm = new LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService();
            llm.EnqueueResult(CreateSucceededLlmResult("LLM 不应覆盖", "LLM 不应覆盖简介", seasonNumber: 1));
            var doubanApi = new DoubanApi(this.loggerFactory);
            SeedDoubanSubject(doubanApi, new DoubanSubject
            {
                Sid = "season-douban-1",
                Name = "豆瓣权威季名",
                Intro = "豆瓣权威简介",
                Genre = "剧情 / 动画",
                Year = 2024,
                Screen = "2024-01-01",
            });
            var provider = this.CreateProvider(LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(), llm, doubanApi: doubanApi);
            var info = CreateExternalMissingInfo(indexNumber: 1);
            info.ProviderIds = new Dictionary<string, string> { { BaseProvider.DoubanProviderId, "season-douban-1" } };
            info.SeriesProviderIds = new Dictionary<string, string> { { BaseProvider.DoubanProviderId, "series-douban" } };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("豆瓣权威季名", result.Item!.Name);
            Assert.AreEqual("豆瓣权威简介", result.Item.Overview);
            Assert.AreEqual("season-douban-1", result.Item.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.AreEqual(0, llm.Requests.Count);
        }

        [TestMethod]
        public async Task GetMetadata_WhenTmdbSeasonTitleExists_DoesNotCallOrOverwriteWithLlm()
        {
            var configuration = CreateBaseConfiguration();
            configuration.EnableTmdb = true;
            ReplacePluginConfiguration(configuration);
            var llm = new LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService();
            llm.EnqueueResult(CreateSucceededLlmResult("LLM 不应覆盖 TMDb", "LLM 不应覆盖简介", seasonNumber: 2));
            var tmdbApi = new TmdbApi(this.loggerFactory);
            SeedTmdbSeason(tmdbApi, 34860, 2, "zh-CN", "TMDb 权威季名", "TMDb 权威简介");
            var provider = this.CreateProvider(LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(), llm, tmdbApi: tmdbApi);
            var info = CreateExternalMissingInfo();
            info.SeriesProviderIds = new Dictionary<string, string> { { MetadataProvider.Tmdb.ToString(), "34860" } };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("TMDb 权威季名", result.Item!.Name);
            Assert.AreEqual("TMDb 权威简介", result.Item.Overview);
            Assert.AreEqual(2, result.Item.IndexNumber);
            Assert.AreEqual(0, llm.Requests.Count);
        }

        private SeasonProvider CreateProvider(
            IHttpContextAccessor httpContextAccessor,
            LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService llm,
            DoubanApi? doubanApi = null,
            TmdbApi? tmdbApi = null)
        {
            return new SeasonProvider(
                new DefaultHttpClientFactory(),
                this.loggerFactory,
                new Mock<ILibraryManager>().Object,
                httpContextAccessor,
                doubanApi ?? new DoubanApi(this.loggerFactory),
                tmdbApi ?? new TmdbApi(this.loggerFactory),
                new OmdbApi(this.loggerFactory),
                new ImdbApi(this.loggerFactory),
                llm);
        }

        private static SeasonInfo CreateExternalMissingInfo(int? indexNumber = 2)
        {
            return new SeasonInfo
            {
                Name = "示例剧 第2季",
                Path = "/mnt/media/TV/示例剧/Season 02",
                IndexNumber = indexNumber,
                MetadataLanguage = "zh-CN",
                IsAutomated = false,
                ProviderIds = new Dictionary<string, string>
                {
                    { MetadataProvider.Tvdb.ToString(), "tvdb-season-existing" },
                },
                SeriesProviderIds = new Dictionary<string, string>(),
            };
        }

        private static LlmScrapingAssistResult CreateSucceededLlmResult(string title, string overview, int seasonNumber)
        {
            return LlmScrapingAssistResult.Succeeded(
                new LlmPromptContext
                {
                    MediaType = "Season",
                    RelativePath = "TV/示例剧/Season 02",
                    SeasonNumber = 2,
                },
                new LlmScrapingSuggestion
                {
                    MediaType = "Season",
                    Title = title,
                    Overview = overview,
                    SeasonNumber = seasonNumber,
                    Confidence = 0.96,
                },
                new LlmSearchHints
                {
                    Title = title,
                });
        }

        private static void AssertLlmTextResult(MetadataResult<Season> result, int expectedIndexNumber)
        {
            Assert.IsTrue(result.HasMetadata);
            Assert.IsNotNull(result.Item);
            Assert.AreEqual(expectedIndexNumber, result.Item!.IndexNumber);
        }

        private static void AssertNoProviderIds(Season item)
        {
            Assert.IsTrue(item.ProviderIds == null || item.ProviderIds.Count == 0, "LLM 纯文本补全不得写入任何 ProviderIds。 ");
            Assert.IsNull(item.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.IsNull(item.GetProviderId(MetadataProvider.Tmdb));
            Assert.IsNull(item.GetProviderId(MetadataProvider.Tvdb));
            Assert.IsNull(item.GetProviderId(MetaSharkPlugin.ProviderId));
        }

        private static void AssertSeasonLlmRequestIsSafe(LlmScrapingAssistRequest request)
        {
            Assert.AreEqual("Season", request.MediaType);
            Assert.AreEqual(DefaultScraperSemantic.UserRefresh, request.Semantic);
            Assert.IsNotNull(request.LookupInfo);
            Assert.AreEqual("TV/示例剧/Season 02", request.LookupInfo!.Path);
            Assert.AreEqual(2, request.LookupInfo.IndexNumber);
            LlmProviderFlowTestHelpers.AssertNoSensitiveContent(request, null, request.LookupInfo.Path);
            Assert.AreEqual("present", request.LookupInfo.ProviderIds![MetadataProvider.Tvdb.ToString()]);
            Assert.IsFalse(request.LookupInfo.ProviderIds.ContainsValue("tvdb-season-existing"));
        }

        private static PluginConfiguration CreateBaseConfiguration(bool enableLlmAssist = true)
        {
            return new PluginConfiguration
            {
                EnableTmdb = false,
                EnableLlmAssist = enableLlmAssist,
                LlmBaseUrl = "http://127.0.0.1:11434/v1",
                LlmApiKey = "test-key",
                LlmModel = "test-model",
                LlmAllowTextCompletion = true,
                LlmConfidenceThreshold = 0.75,
            };
        }

        private static void SeedDoubanSubject(DoubanApi doubanApi, DoubanSubject subject)
        {
            var cache = GetMemoryCache(doubanApi, typeof(DoubanApi));
            cache.Set($"movie_{subject.Sid}", subject, TimeSpan.FromMinutes(5));
            cache.Set($"celebrities_{subject.Sid}", new List<DoubanCelebrity>(), TimeSpan.FromMinutes(5));
        }

        private static void SeedTmdbSeason(TmdbApi tmdbApi, int seriesTmdbId, int seasonNumber, string language, string seasonName, string overview)
        {
            GetMemoryCache(tmdbApi, typeof(TmdbApi)).Set(
                $"season-{seriesTmdbId}-s{seasonNumber}-{language}-{language}",
                new TvSeason
                {
                    Name = seasonName,
                    Overview = overview,
                    AirDate = new DateTime(2024, 1, 1),
                },
                TimeSpan.FromMinutes(5));
        }

        private static IMemoryCache GetMemoryCache(object api, Type apiType)
        {
            var memoryCacheField = apiType.GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(memoryCacheField, $"{apiType.Name}.memoryCache 未定义");
            var memoryCache = memoryCacheField!.GetValue(api) as IMemoryCache;
            Assert.IsNotNull(memoryCache, $"{apiType.Name}.memoryCache 不是有效的 IMemoryCache");
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
