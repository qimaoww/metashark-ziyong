using System.Collections;
using System.Globalization;
using System.Reflection;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaShark;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.EpisodeGroupMapping;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Providers.Llm;
using Jellyfin.Plugin.MetaShark.Test.EpisodeGroupMapping;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using TMDbLib.Objects.General;
using TMDbLib.Objects.Search;
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [DoNotParallelize]
    public class SeriesProviderLlmAssistTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-series-provider-llm-tests");
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
        public async Task GetMetadata_LlmDisabled_DoesNotCallAssist()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableLlm: false));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var llmService = new LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService();
            var provider = CreateProvider(loggerFactory, LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor("disabled"), llmService: llmService);

            var result = await provider.GetMetadata(CreateSeriesInfo(), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.HasMetadata);
            Assert.AreEqual(0, llmService.Requests.Count);
        }

        [TestMethod]
        public async Task GetMetadata_AutomaticRefresh_DoesNotCallAssist()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var llmService = new LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService();
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            var provider = CreateProvider(loggerFactory, LlmProviderFlowTestHelpers.CreateAutomaticRefreshContextAccessor(), llmService: llmService, llmExternalIdResolutionService: externalIdService);
            var info = CreateSeriesInfo();
            info.IsAutomated = true;

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.HasMetadata);
            Assert.AreEqual(0, llmService.Requests.Count);
            Assert.AreEqual(0, externalIdService.Requests.Count);
        }

        [TestMethod]
        public async Task GetMetadata_OverwriteRefresh_DoesNotCallExternalIdResolverOrEpisodeGroupMappingAssist()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableEpisodeGroupMappingAssist: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 8870, "zh-CN", CreateTmdbSeries(8870, "覆盖剧集", "覆盖简介", null, null));
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueResult(CreateExternalIdResolutionResult("TMDb", "9999", "Series", MetadataProvider.Tmdb.ToString()));
            var llmApi = new RecordingLlmApi("overwrite-group", 0.95);
            var refreshCalls = new List<QueueRefreshCall>();
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateExplicitRefreshContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), replaceAllMetadata: true),
                tmdbApi: tmdbApi,
                llmEpisodeGroupMappingProviderAssistService: CreateEpisodeGroupMappingProviderAssistService(loggerFactory, llmApi, tmdbApi, Array.Empty<BaseItem>(), refreshCalls),
                llmExternalIdResolutionService: externalIdService);
            var info = CreateSeriesInfo("覆盖剧集");
            info.ProviderIds![MetadataProvider.Tmdb.ToString()] = "8870";

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("8870", result.Item!.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual(0, externalIdService.Requests.Count);
            Assert.AreEqual(0, llmApi.Prompts.Count);
            Assert.AreEqual(0, refreshCalls.Count);
        }

        [TestMethod]
        public async Task GetMetadata_ManualMatch_CallsAssistOnceAndUsesHintForDoubanSearch()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var hintedSubject = CreateDoubanSubject("llm-douban-series", "LLM 正确剧集", 2024);
            SeedDoubanSearchResults(doubanApi, "LLM 正确剧集", new[] { hintedSubject });
            SeedDoubanSubject(doubanApi, hintedSubject);
            var llmService = CreateLlmService("LLM 正确剧集", 2024);
            var provider = CreateProvider(loggerFactory, LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor("manual"), doubanApi: doubanApi, llmService: llmService);

            var result = await provider.GetMetadata(CreateSeriesInfo("错误剧集"), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, llmService.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("LLM 正确剧集", result.Item!.Name);
            Assert.AreEqual("llm-douban-series", result.Item.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Douban_llm-douban-series", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
        }

        [TestMethod]
        public async Task GetMetadata_ExplicitSearchMissing_CallsAssistOnce()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var hintedSubject = CreateDoubanSubject("search-missing-douban-series", "缺失搜索剧集", 2023);
            SeedDoubanSearchResults(doubanApi, "缺失搜索剧集", new[] { hintedSubject });
            SeedDoubanSubject(doubanApi, hintedSubject);
            var itemId = Guid.NewGuid();
            var llmService = CreateLlmService("缺失搜索剧集", 2023);
            var provider = CreateProvider(loggerFactory, LlmProviderFlowTestHelpers.CreateExplicitSearchMissingContextAccessor(itemId.ToString("N", CultureInfo.InvariantCulture)), doubanApi: doubanApi, llmService: llmService);
            var info = CreateSeriesInfo("原始缺失剧集");
            info.IsAutomated = false;

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, llmService.Requests.Count);
            Assert.AreEqual(DefaultScraperSemantic.UserRefresh, llmService.Requests[0].Semantic);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("search-missing-douban-series", result.Item!.GetProviderId(BaseProvider.DoubanProviderId));
        }

        [TestMethod]
        public async Task GetMetadata_LlmFailure_FallsBackToDeterministicSearch()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var deterministicSubject = CreateDoubanSubject("deterministic-douban-series", "确定性剧集", 2022);
            SeedDoubanSearchResults(doubanApi, "确定性剧集", new[] { deterministicSubject });
            SeedDoubanSubject(doubanApi, deterministicSubject);
            var llmService = new LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService();
            llmService.EnqueueResult(LlmScrapingAssistResult.Failed("test failure"));
            var provider = CreateProvider(loggerFactory, LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor("failure"), doubanApi: doubanApi, llmService: llmService);

            var result = await provider.GetMetadata(CreateSeriesInfo("确定性剧集", "/library/tv/确定性剧集"), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, llmService.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("deterministic-douban-series", result.Item!.GetProviderId(BaseProvider.DoubanProviderId));
        }

        [TestMethod]
        public async Task GetMetadata_LlmRequest_UsesSafeRelativePathOnly()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var llmService = CreateLlmService("安全剧集", 2021);
            var provider = CreateProvider(loggerFactory, LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor("privacy"), llmService: llmService);
            var info = CreateSeriesInfo("绝对路径剧集", "/mnt/media/Shows/安全剧集");

            _ = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, llmService.Requests.Count);
            var request = llmService.Requests[0];
            LlmProviderFlowTestHelpers.AssertNoSensitiveContent(request);
            Assert.AreEqual("Shows/安全剧集", request.LookupInfo!.Path);
            Assert.AreEqual("present", request.LookupInfo.ProviderIds![MetadataProvider.Tmdb.ToString()]);
        }

        [TestMethod]
        public async Task GetMetadata_LlmHintCanSelectSearchInputButProviderIdsComeFromTmdb()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeriesSearchResults(
                tmdbApi,
                "LLM TMDb 剧集",
                "zh-CN",
                new SearchTv
                {
                    Id = 8801,
                    Name = "LLM TMDb 剧集",
                    OriginalName = "LLM TMDb Series",
                    FirstAirDate = new DateTime(2020, 1, 1),
                });
            SeedTmdbSeries(tmdbApi, 8801, "zh-CN", CreateTmdbSeries(8801, "TMDb 权威剧集", "TMDb authoritative overview", "tt8801", "9901"));
            var llmService = CreateLlmService("LLM TMDb 剧集", 2020);
            var provider = CreateProvider(loggerFactory, LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor("tmdb"), tmdbApi: tmdbApi, llmService: llmService);
            var info = CreateSeriesInfo("错误 TMDb 剧集");

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, llmService.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("8801", result.Item!.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("Tmdb_8801", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual("9901", result.Item.GetProviderId(MetadataProvider.Tvdb));
            Assert.AreEqual("tt8801", result.Item.GetProviderId(MetadataProvider.Imdb));
            Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId));
        }

        [TestMethod]
        public async Task GetMetadata_DeterministicFolderTitleYearMismatch_UsesLlmHintForRequery()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeriesSearchResults(
                tmdbApi,
                "真实剧集",
                "zh-CN",
                new SearchTv
                {
                    Id = 8810,
                    Name = "真实剧集",
                    OriginalName = "Real Series",
                    FirstAirDate = new DateTime(2024, 1, 1),
                });
            SeedTmdbSeries(tmdbApi, 8810, "zh-CN", CreateTmdbSeries(8810, "真实剧集", "真实剧集简介", null, null));
            var llmService = CreateLlmService("真实剧集", 2024);
            var provider = CreateProvider(loggerFactory, LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor("mismatch"), tmdbApi: tmdbApi, llmService: llmService);
            var info = CreateSeriesInfo("错误标题", "/library/tv/错误标题 2018");
            info.Year = 2018;

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, llmService.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("真实剧集", result.Item!.Name);
            Assert.AreEqual("8810", result.Item.GetProviderId(MetadataProvider.Tmdb));
        }

        [TestMethod]
        public async Task GetMetadata_LlmEnabled_DoesNotChangePeopleOverwriteCandidateCounting()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            var seededSeries = CreateTmdbSeries(8820, "人物候选剧集", "人物候选简介", null, null);
            SetTmdbSeriesCredits(
                seededSeries,
                new[]
                {
                    CreateCastCredit(8101, "Accepted Actor", "角色A", 0),
                    CreateCastCredit(8102, "Rejected Actor", "角色B", 1),
                },
                Array.Empty<Dictionary<string, object?>>());
            SeedTmdbSeries(tmdbApi, 8820, "zh-CN", seededSeries);
            SeedTmdbPerson(tmdbApi, 8101, "可接受演员", language: "zh-CN");
            SeedTmdbPerson(tmdbApi, 8102, string.Empty, language: "zh-CN");
            var store = new InMemoryMovieSeriesPeopleOverwriteRefreshCandidateStore();
            var info = CreateSeriesInfo("人物候选剧集", "/library/tv/人物候选剧集");
            info.ProviderIds = new Dictionary<string, string>
            {
                [MetaSharkPlugin.ProviderId] = "Tmdb_8820",
                [MetadataProvider.Tmdb.ToString()] = "8820",
            };
            var currentSeries = new LlmAuthoritativeTrackingSeries
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };
            currentSeries.SetSimulatedPeople(new[]
            {
                new PersonInfo
                {
                    Name = "旧演员",
                    Type = PersonKind.Actor,
                },
            });
            var libraryManagerStub = CreateLibraryManager(currentSeries);
            var llmService = CreateLlmService("LLM 不应影响人物", 2024);
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(currentSeries.Id.ToString("N", CultureInfo.InvariantCulture)),
                libraryManager: libraryManagerStub.Object,
                tmdbApi: tmdbApi,
                store: store,
                llmService: llmService);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, llmService.Requests.Count, "已有权威 TMDb metadata 时不应调用 LLM。 ");
            Assert.AreEqual(1, result.People?.Count ?? 0, "LLM 开启时人物计数仍应只统计 TMDb 接受演员。 ");
            var candidate = store.Peek(currentSeries.Id);
            Assert.IsNotNull(candidate);
            Assert.AreEqual(1, candidate!.ExpectedPeopleCount);
        }

        [TestMethod]
        public async Task GetMetadata_LlmTextCompletion_FillsOnlyEmptyAllowedFields()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(allowTextCompletion: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeriesSearchResults(
                tmdbApi,
                "文本补全剧集",
                "zh-CN",
                new SearchTv
                {
                    Id = 8830,
                    Name = "文本补全剧集",
                    OriginalName = "Text Completion Series",
                    FirstAirDate = new DateTime(2024, 3, 1),
                });
            SeedTmdbSeries(tmdbApi, 8830, "zh-CN", CreateTmdbSeries(8830, string.Empty, string.Empty, null, null));
            var llmService = new LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService();
            llmService.EnqueueResult(LlmScrapingAssistResult.Succeeded(
                new LlmPromptContext { MediaType = "Series", RelativePath = "Shows/文本补全剧集" },
                new LlmScrapingSuggestion
                {
                    MediaType = "Series",
                    Title = "文本补全剧集",
                    OriginalTitle = "Text Completion Series",
                    Overview = "LLM 仅补空白简介",
                    Year = 2024,
                    Confidence = 0.95,
                },
                new LlmSearchHints
                {
                    Title = "文本补全剧集",
                    Year = 2024,
                }));
            var provider = CreateProvider(loggerFactory, LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor("text"), tmdbApi: tmdbApi, llmService: llmService);

            var result = await provider.GetMetadata(CreateSeriesInfo("错误文本补全剧集"), CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("文本补全剧集", result.Item!.Name);
            Assert.AreEqual("Text Completion Series", result.Item.OriginalTitle);
            Assert.AreEqual("LLM 仅补空白简介", result.Item.Overview);
            Assert.AreEqual("8830", result.Item.GetProviderId(MetadataProvider.Tmdb));
        }

        [TestMethod]
        public async Task GetMetadata_ManualTmdbMatch_InvokesEpisodeGroupMappingAssistAndQueuesAffectedSeriesRefresh()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableEpisodeGroupMappingAssist: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            var tvShow = CreateTmdbSeries(8840, "映射剧集", "映射简介", null, null);
            tvShow.EpisodeGroups = new ResultContainer<TvGroupCollection>
            {
                Results = new List<TvGroupCollection>
                {
                    new TvGroupCollection
                    {
                        Id = "candidate-group",
                        Name = "候选剧集组",
                        Type = TvGroupType.Absolute,
                        GroupCount = 1,
                        EpisodeCount = 1,
                    },
                },
            };
            SeedTmdbSeries(tmdbApi, 8840, "zh-CN", tvShow);
            ExplicitEpisodeGroupMappingTestHelper.SeedEpisodeGroupById(tmdbApi, "candidate-group", "zh-CN");
            var llmApi = new RecordingLlmApi("candidate-group", 0.95);
            var mappedSeries = CreateSeries(Guid.NewGuid(), "映射剧集", "8840");
            var refreshCalls = new List<QueueRefreshCall>();
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(mappedSeries.Id.ToString("N", CultureInfo.InvariantCulture)),
                libraryManager: CreateLibraryManager(new[] { mappedSeries }).Object,
                tmdbApi: tmdbApi,
                llmEpisodeGroupMappingProviderAssistService: CreateEpisodeGroupMappingProviderAssistService(loggerFactory, llmApi, tmdbApi, new[] { mappedSeries }, refreshCalls));
            var info = CreateSeriesInfo("映射剧集", "/mnt/media/TV/映射剧集");
            info.ProviderIds = new Dictionary<string, string>
            {
                [MetaSharkPlugin.ProviderId] = "Tmdb_8840",
                [MetadataProvider.Tmdb.ToString()] = "8840",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual(1, llmApi.Prompts.Count);
            Assert.AreEqual("8840=candidate-group", MetaSharkPlugin.Instance!.Configuration.TmdbEpisodeGroupMap);
            AssertQueuedSeries(refreshCalls, mappedSeries.Id);
            LlmProviderFlowTestHelpers.AssertNoSensitiveContent(llmApi.Prompts.Single());
            StringAssert.Contains(llmApi.Prompts.Single(), "seriesTmdbId");
            StringAssert.Contains(llmApi.Prompts.Single(), "candidate-group");
        }

        [TestMethod]
        public async Task GetMetadata_AutomaticRefresh_DoesNotInvokeEpisodeGroupMappingAssist()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableEpisodeGroupMappingAssist: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 8850, "zh-CN", CreateTmdbSeries(8850, "自动剧集", "自动简介", null, null));
            var llmApi = new RecordingLlmApi("candidate-group", 0.95);
            var refreshCalls = new List<QueueRefreshCall>();
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateAutomaticRefreshContextAccessor(),
                tmdbApi: tmdbApi,
                llmEpisodeGroupMappingProviderAssistService: CreateEpisodeGroupMappingProviderAssistService(loggerFactory, llmApi, tmdbApi, Array.Empty<BaseItem>(), refreshCalls));
            var info = CreateSeriesInfo("自动剧集");
            info.IsAutomated = true;
            info.ProviderIds = new Dictionary<string, string>
            {
                [MetaSharkPlugin.ProviderId] = "Tmdb_8850",
                [MetadataProvider.Tmdb.ToString()] = "8850",
            };

            _ = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, llmApi.Prompts.Count);
            Assert.AreEqual(string.Empty, MetaSharkPlugin.Instance!.Configuration.TmdbEpisodeGroupMap);
            Assert.AreEqual(0, refreshCalls.Count);
        }

        [TestMethod]
        public async Task GetMetadata_UserRefreshWithoutHttpContext_DoesNotCallExternalIdResolver()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableEpisodeGroupMappingAssist: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var doubanSubject = CreateDoubanSubject("null-http-series-douban", "空上下文剧集", 2024);
            SeedDoubanSubject(doubanApi, doubanSubject);
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueResult(CreateExternalIdResolutionResult("TMDb", "8871", "Series", MetadataProvider.Tmdb.ToString()));
            var provider = CreateProvider(
                loggerFactory,
                new HttpContextAccessor { HttpContext = null },
                doubanApi: doubanApi,
                llmExternalIdResolutionService: externalIdService);
            var info = CreateSeriesInfo("空上下文剧集", "/mnt/media/TV/空上下文剧集");
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "null-http-series-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_null-http-series-douban",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, externalIdService.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("null-http-series-douban", result.Item!.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.Item.GetProviderId(MetadataProvider.Tmdb)));
        }

        [TestMethod]
        public async Task GetMetadata_DoubanMetadataWithoutTmdbId_InvokesExternalIdResolverAndUsesVerifiedTmdb()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableEpisodeGroupMappingAssist: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var doubanSubject = CreateDoubanSubject("1291843", "豆瓣已有剧集", 2024);
            SeedDoubanSubject(doubanApi, doubanSubject);
            var tmdbApi = new TmdbApi(loggerFactory);
            var tvShow = CreateTmdbSeries(8860, "豆瓣已有剧集", "TMDb 补充简介", "tt8860000", "886000");
            tvShow.EpisodeGroups = new ResultContainer<TvGroupCollection>
            {
                Results = new List<TvGroupCollection>
                {
                    new TvGroupCollection
                    {
                        Id = "douban-verified-group",
                        Name = "豆瓣补充映射组",
                        Type = TvGroupType.Absolute,
                        GroupCount = 1,
                        EpisodeCount = 1,
                    },
                },
            };
            SeedTmdbSeries(tmdbApi, 8860, "zh-CN", tvShow);
            ExplicitEpisodeGroupMappingTestHelper.SeedEpisodeGroupById(tmdbApi, "douban-verified-group", "zh-CN");
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueResult(CreateExternalIdResolutionResult("TMDb", "8860", "Series", MetadataProvider.Tmdb.ToString()));
            var llmApi = new RecordingLlmApi("douban-verified-group", 0.95);
            var mappedSeries = CreateSeries(Guid.NewGuid(), "豆瓣已有剧集", "8860");
            var refreshCalls = new List<QueueRefreshCall>();
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(mappedSeries.Id.ToString("N", CultureInfo.InvariantCulture)),
                libraryManager: CreateLibraryManager(new[] { mappedSeries }).Object,
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmEpisodeGroupMappingProviderAssistService: CreateEpisodeGroupMappingProviderAssistService(loggerFactory, llmApi, tmdbApi, new[] { mappedSeries }, refreshCalls),
                llmExternalIdResolutionService: externalIdService);
            var info = CreateSeriesInfo("豆瓣已有剧集", "/mnt/media/TV/豆瓣已有剧集");
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "1291843",
                [MetaSharkPlugin.ProviderId] = "Douban_1291843",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdService.Requests.Count, "已有 Douban metadata 但缺 TMDb 时仍应尝试外部 ID 解析。 ");
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("1291843", result.Item!.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.AreEqual("8860", result.Item.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("8860", info.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("tt8860000", result.Item.GetProviderId(MetadataProvider.Imdb));
            Assert.AreEqual("886000", result.Item.GetProviderId(MetadataProvider.Tvdb));
            Assert.AreEqual(1, llmApi.Prompts.Count, "剧集组映射只能在已验证 TMDbId 写入后运行。 ");
            AssertQueuedSeries(refreshCalls, mappedSeries.Id);
        }

        [TestMethod]
        public async Task GetMetadata_ExternalIdResolverRequest_CarriesExistingPublicProviderIdsAsContext()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var doubanSubject = CreateDoubanSubject("1291843", "上下文剧集", 2024);
            SeedDoubanSubject(doubanApi, doubanSubject);
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueResult(LlmExternalIdResolutionResult.VerificationFailed("test no verified candidate"));
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor("context"),
                doubanApi: doubanApi,
                llmExternalIdResolutionService: externalIdService);
            var info = CreateSeriesInfo("上下文剧集", "/mnt/media/Shows/上下文剧集");
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "1291843",
                [MetadataProvider.Imdb.ToString()] = "tt0133093",
                [MetadataProvider.Tvdb.ToString()] = "81189",
                [MetaSharkPlugin.ProviderId] = "Douban_1291843",
                ["apiKey"] = "sk-test-secret",
            };

            _ = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdService.Requests.Count);
            var requestProviderIds = externalIdService.Requests[0].LookupInfo!.ProviderIds!;
            Assert.AreEqual("1291843", requestProviderIds[BaseProvider.DoubanProviderId]);
            Assert.AreEqual("tt0133093", requestProviderIds[MetadataProvider.Imdb.ToString()]);
            Assert.AreEqual("81189", requestProviderIds[MetadataProvider.Tvdb.ToString()]);
            Assert.IsFalse(requestProviderIds.ContainsKey(MetaSharkPlugin.ProviderId));
            Assert.IsFalse(requestProviderIds.ContainsKey("apiKey"));
            Assert.AreEqual("Shows/上下文剧集", externalIdService.Requests[0].LookupInfo!.Path);
        }

        [TestMethod]
        public async Task GetMetadata_ExternalIdResolverVerifiedTmdb_WritesMissingTmdbAndUsesTmdbMetadata()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 8861, "zh-CN", CreateTmdbSeries(8861, "解析剧集", "解析剧集简介", null, null));
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueResult(CreateExternalIdResolutionResult("TMDb", "8861", "Series", MetadataProvider.Tmdb.ToString()));
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor("resolver-tmdb"),
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdService);
            var info = CreateSeriesInfo("解析剧集", "/mnt/media/TV/解析剧集");

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdService.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("8861", info.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("8861", result.Item!.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("解析剧集", result.Item.Name);
        }

        [TestMethod]
        public async Task GetMetadata_ExistingTmdbId_DoesNotInvokeExternalIdResolverOrOverwrite()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 8862, "zh-CN", CreateTmdbSeries(8862, "已有 TMDb 剧集", "已有 TMDb 简介", null, null));
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueResult(CreateExternalIdResolutionResult("TMDb", "9999", "Series", MetadataProvider.Tmdb.ToString()));
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor("existing-tmdb"),
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdService);
            var info = CreateSeriesInfo("已有 TMDb 剧集", "/mnt/media/TV/已有 TMDb 剧集");
            info.ProviderIds![MetadataProvider.Tmdb.ToString()] = "8862";

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, externalIdService.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("8862", info.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("8862", result.Item!.GetProviderId(MetadataProvider.Tmdb));
        }

        [TestMethod]
        public async Task GetMetadata_ExplicitSearchMissingVerifiedTmdb_InvokesEpisodeGroupMappingAfterResolution()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableEpisodeGroupMappingAssist: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            var tvShow = CreateTmdbSeries(8863, "缺失映射剧集", "缺失映射简介", null, null);
            tvShow.EpisodeGroups = new ResultContainer<TvGroupCollection>
            {
                Results = new List<TvGroupCollection>
                {
                    new TvGroupCollection
                    {
                        Id = "resolved-group",
                        Name = "解析映射组",
                        Type = TvGroupType.Absolute,
                        GroupCount = 1,
                        EpisodeCount = 1,
                    },
                },
            };
            SeedTmdbSeries(tmdbApi, 8863, "zh-CN", tvShow);
            ExplicitEpisodeGroupMappingTestHelper.SeedEpisodeGroupById(tmdbApi, "resolved-group", "zh-CN");
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueResult(CreateExternalIdResolutionResult("TMDb", "8863", "Series", MetadataProvider.Tmdb.ToString()));
            var llmApi = new RecordingLlmApi("resolved-group", 0.95);
            var mappedSeries = CreateSeries(Guid.NewGuid(), "缺失映射剧集", "8863");
            var refreshCalls = new List<QueueRefreshCall>();
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(mappedSeries.Id.ToString("N", CultureInfo.InvariantCulture)),
                libraryManager: CreateLibraryManager(new[] { mappedSeries }).Object,
                tmdbApi: tmdbApi,
                llmEpisodeGroupMappingProviderAssistService: CreateEpisodeGroupMappingProviderAssistService(loggerFactory, llmApi, tmdbApi, new[] { mappedSeries }, refreshCalls),
                llmExternalIdResolutionService: externalIdService);
            var info = CreateSeriesInfo("缺失映射剧集", "/mnt/media/TV/缺失映射剧集");

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("8863", result.Item!.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual(1, externalIdService.Requests.Count);
            Assert.AreEqual(1, llmApi.Prompts.Count);
            AssertQueuedSeries(refreshCalls, mappedSeries.Id);
        }

        [TestMethod]
        public async Task GetMetadata_FailedExternalIdResolver_DoesNotInvokeEpisodeGroupMapping()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableEpisodeGroupMappingAssist: true, enableTmdbMatch: false));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueResult(LlmExternalIdResolutionResult.VerificationFailed("test unverified candidate"));
            var llmApi = new RecordingLlmApi("unverified-group", 0.95);
            var refreshCalls = new List<QueueRefreshCall>();
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor("failed-resolver"),
                tmdbApi: tmdbApi,
                llmEpisodeGroupMappingProviderAssistService: CreateEpisodeGroupMappingProviderAssistService(loggerFactory, llmApi, tmdbApi, Array.Empty<BaseItem>(), refreshCalls),
                llmExternalIdResolutionService: externalIdService);

            var result = await provider.GetMetadata(CreateSeriesInfo("未验证映射剧集"), CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(result.HasMetadata);
            Assert.AreEqual(1, externalIdService.Requests.Count);
            Assert.AreEqual(0, llmApi.Prompts.Count);
            Assert.AreEqual(0, refreshCalls.Count);
        }

        [TestMethod]
        public async Task GetMetadata_UnverifiedExternalIdResolverWithExistingDouban_DoesNotInvokeEpisodeGroupMapping()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableEpisodeGroupMappingAssist: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var doubanSubject = CreateDoubanSubject("1291844", "未验证豆瓣剧集", 2024);
            SeedDoubanSubject(doubanApi, doubanSubject);
            var tmdbApi = new TmdbApi(loggerFactory);
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueResult(LlmExternalIdResolutionResult.VerificationFailed("test unverified tmdb candidate"));
            var llmApi = new RecordingLlmApi("unverified-group", 0.95);
            var refreshCalls = new List<QueueRefreshCall>();
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor("unverified-douban"),
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmEpisodeGroupMappingProviderAssistService: CreateEpisodeGroupMappingProviderAssistService(loggerFactory, llmApi, tmdbApi, Array.Empty<BaseItem>(), refreshCalls),
                llmExternalIdResolutionService: externalIdService);
            var info = CreateSeriesInfo("未验证豆瓣剧集", "/mnt/media/TV/未验证豆瓣剧集");
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "1291844",
                [MetaSharkPlugin.ProviderId] = "Douban_1291844",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("1291844", result.Item!.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.IsNull(result.Item.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual(1, externalIdService.Requests.Count);
            Assert.AreEqual(0, llmApi.Prompts.Count);
            Assert.AreEqual(0, refreshCalls.Count);
        }

        private static SeriesProvider CreateProvider(
            ILoggerFactory loggerFactory,
            IHttpContextAccessor httpContextAccessor,
            ILibraryManager? libraryManager = null,
            DoubanApi? doubanApi = null,
            TmdbApi? tmdbApi = null,
            IMovieSeriesPeopleOverwriteRefreshCandidateStore? store = null,
            LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService? llmService = null,
            ILlmEpisodeGroupMappingProviderAssistService? llmEpisodeGroupMappingProviderAssistService = null,
            ILlmExternalIdResolutionService? llmExternalIdResolutionService = null)
        {
            return new SeriesProvider(
                new DefaultHttpClientFactory(),
                loggerFactory,
                libraryManager ?? new Mock<ILibraryManager>().Object,
                httpContextAccessor,
                doubanApi ?? new DoubanApi(loggerFactory),
                tmdbApi ?? new TmdbApi(loggerFactory),
                new OmdbApi(loggerFactory),
                new ImdbApi(loggerFactory),
                store,
                llmService,
                llmEpisodeGroupMappingProviderAssistService,
                llmExternalIdResolutionService);
        }

        private static LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService CreateLlmService(string title, int year)
        {
            var llmService = new LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService();
            llmService.EnqueueResult(LlmScrapingAssistResult.Succeeded(
                new LlmPromptContext
                {
                    MediaType = "Series",
                    RelativePath = $"Shows/{title}",
                    Name = title,
                    Year = year,
                },
                new LlmScrapingSuggestion
                {
                    MediaType = "Series",
                    Title = title,
                    Year = year,
                    Confidence = 0.95,
                },
                new LlmSearchHints
                {
                    Title = title,
                    Year = year,
                }));
            return llmService;
        }

        private static PluginConfiguration CreateLlmConfiguration(bool enableLlm = true, bool allowTextCompletion = false, bool enableEpisodeGroupMappingAssist = false, string defaultScraperMode = PluginConfiguration.DefaultScraperModeDefault, bool enableTmdbMatch = true)
        {
            return new PluginConfiguration
            {
                EnableLlmAssist = enableLlm,
                EnableLlmEpisodeGroupMappingAssist = enableEpisodeGroupMappingAssist,
                LlmBaseUrl = "https://llm.local/v1",
                LlmApiKey = "sk-test-secret",
                LlmModel = "test-model",
                LlmAllowTextCompletion = allowTextCompletion,
                LlmConfidenceThreshold = 0.75,
                EnableTmdbMatch = enableTmdbMatch,
                DefaultScraperMode = defaultScraperMode,
            };
        }

        private static LlmExternalIdResolutionResult CreateExternalIdResolutionResult(string provider, string id, string mediaType, string providerIdKey)
        {
            var candidate = new LlmExternalIdCandidate
            {
                Provider = provider,
                Id = id,
                MediaType = mediaType,
                Confidence = 0.95,
                Reason = "test verified candidate",
                Evidence = "test verified evidence",
            };

            return LlmExternalIdResolutionResult.Succeeded(
                new[] { candidate },
                new[] { new LlmExternalIdProviderIdWrite(providerIdKey, provider, id, mediaType, candidate) },
                Array.Empty<LlmExternalIdProviderIdWrite>(),
                "test verified");
        }

        private static Series CreateSeries(Guid id, string name, string tmdbId)
        {
            return new Series
            {
                Id = id,
                Name = name,
                ProviderIds = new Dictionary<string, string>
                {
                    [MetadataProvider.Tmdb.ToString()] = tmdbId,
                },
            };
        }

        private static LlmEpisodeGroupMappingProviderAssistService CreateEpisodeGroupMappingProviderAssistService(
            ILoggerFactory loggerFactory,
            RecordingLlmApi llmApi,
            TmdbApi tmdbApi,
            IReadOnlyCollection<BaseItem> libraryItems,
            List<QueueRefreshCall> queueCalls)
        {
            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(libraryItems.ToList());

            var providerManagerStub = new Mock<IProviderManager>();
            providerManagerStub
                .Setup(x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()))
                .Callback<Guid, MetadataRefreshOptions, RefreshPriority>((itemId, options, priority) => queueCalls.Add(new QueueRefreshCall(itemId, options, priority)));

            return new LlmEpisodeGroupMappingProviderAssistService(
                new LlmEpisodeGroupMappingAssistService(llmApi, tmdbApi),
                tmdbApi,
                libraryManagerStub.Object,
                providerManagerStub.Object,
                Mock.Of<IFileSystem>(),
                new LlmAssistTriggerPolicy(),
                new EpisodeGroupRefreshService(),
                loggerFactory.CreateLogger<LlmEpisodeGroupMappingProviderAssistService>());
        }

        private static void AssertQueuedSeries(IReadOnlyCollection<QueueRefreshCall> queueCalls, params Guid[] expectedIds)
        {
            CollectionAssert.AreEquivalent(expectedIds, queueCalls.Select(x => x.ItemId).ToArray());
            foreach (var queueCall in queueCalls)
            {
                Assert.AreEqual(RefreshPriority.High, queueCall.Priority);
                Assert.AreEqual(MetadataRefreshMode.FullRefresh, queueCall.Options.MetadataRefreshMode);
                Assert.AreEqual(MetadataRefreshMode.FullRefresh, queueCall.Options.ImageRefreshMode);
                Assert.IsTrue(queueCall.Options.ReplaceAllMetadata);
                Assert.IsFalse(queueCall.Options.ReplaceAllImages);
            }
        }

        private static SeriesInfo CreateSeriesInfo(string name = "测试剧集", string path = "/library/tv/测试剧集")
        {
            return new SeriesInfo
            {
                Name = name,
                Path = path,
                MetadataLanguage = "zh-CN",
                MetadataCountryCode = "CN",
                ProviderIds = new Dictionary<string, string>
                {
                    [MetadataProvider.Tmdb.ToString()] = string.Empty,
                },
            };
        }

        private static DoubanSubject CreateDoubanSubject(string sid, string name, int year)
        {
            return new DoubanSubject
            {
                Sid = sid,
                Name = name,
                OriginalName = name,
                Year = year,
                Category = "电视剧",
                Rating = 8.5f,
                Genre = "剧情",
                Intro = name + "简介",
                Screen = $"{year.ToString(CultureInfo.InvariantCulture)}-01-01",
                Img = "https://img9.doubanio.com/view/photo/s_ratio_poster/public/p0000000001.webp",
            };
        }

        private static TvShow CreateTmdbSeries(int tmdbId, string name, string overview, string? imdbId, string? tvdbId)
        {
            return new TvShow
            {
                Id = tmdbId,
                Name = name,
                OriginalName = name,
                Overview = overview,
                FirstAirDate = new DateTime(2024, 1, 1),
                VoteAverage = 8.0,
                EpisodeRunTime = new List<int>(),
                ContentRatings = new ResultContainer<ContentRating>
                {
                    Results = new List<ContentRating>(),
                },
                ExternalIds = new ExternalIdsTvShow
                {
                    ImdbId = imdbId,
                    TvdbId = tvdbId,
                },
            };
        }

        private static Mock<ILibraryManager> CreateLibraryManager(Series currentSeries)
        {
            var libraryManagerStub = CreateLibraryManager(Array.Empty<BaseItem>());
            libraryManagerStub
                .Setup(x => x.FindByPath(currentSeries.Path, true))
                .Returns(currentSeries);
            return libraryManagerStub;
        }

        private static Mock<ILibraryManager> CreateLibraryManager(IReadOnlyCollection<BaseItem> itemList)
        {
            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(itemList.ToList());
            return libraryManagerStub;
        }

        private sealed record QueueRefreshCall(Guid ItemId, MetadataRefreshOptions Options, RefreshPriority Priority);

        private sealed class RecordingLlmApi : ILlmApi
        {
            private readonly string selectedGroupId;
            private readonly double confidence;

            public RecordingLlmApi(string selectedGroupId, double confidence)
            {
                this.selectedGroupId = selectedGroupId;
                this.confidence = confidence;
            }

            public List<string> Prompts { get; } = new List<string>();

            public Task<LlmApiResult> CompleteAsync(string prompt, CancellationToken cancellationToken)
            {
                this.Prompts.Add(prompt);
                return Task.FromResult(LlmApiResult.Succeeded($"{{\"selectedGroupId\":\"{this.selectedGroupId}\",\"confidence\":{this.confidence.ToString(CultureInfo.InvariantCulture)},\"reason\":\"test\"}}"));
            }
        }

        private sealed class RecordingLlmExternalIdResolutionService : ILlmExternalIdResolutionService
        {
            private readonly Queue<LlmExternalIdResolutionResult> queuedResults = new Queue<LlmExternalIdResolutionResult>();

            public List<LlmExternalIdResolutionRequest> Requests { get; } = new List<LlmExternalIdResolutionRequest>();

            public void EnqueueResult(LlmExternalIdResolutionResult result)
            {
                this.queuedResults.Enqueue(result);
            }

            public Task<LlmExternalIdResolutionResult> ResolveAsync(LlmExternalIdResolutionRequest request, CancellationToken cancellationToken)
            {
                this.Requests.Add(request);
                var result = this.queuedResults.Count > 0
                    ? this.queuedResults.Dequeue()
                    : LlmExternalIdResolutionResult.NotTriggered("No queued external ID test result.");
                return Task.FromResult(result);
            }
        }

        private static void SeedDoubanSubject(DoubanApi doubanApi, DoubanSubject subject)
        {
            var cache = GetDoubanMemoryCache(doubanApi);
            cache.Set($"movie_{subject.Sid}", subject, TimeSpan.FromMinutes(5));
            cache.Set($"celebrities_{subject.Sid}", new List<DoubanCelebrity>(), TimeSpan.FromMinutes(5));
        }

        private static void SeedDoubanSearchResults(DoubanApi doubanApi, string keyword, IReadOnlyCollection<DoubanSubject> subjects)
        {
            GetDoubanMemoryCache(doubanApi).Set(
                $"search_{keyword}",
                subjects.ToList(),
                TimeSpan.FromMinutes(5));
        }

        private static void SeedTmdbSeries(TmdbApi tmdbApi, int tmdbId, string language, TvShow series)
        {
            GetTmdbMemoryCache(tmdbApi).Set(
                $"series-{tmdbId.ToString(CultureInfo.InvariantCulture)}-{language}-{language}",
                series,
                TimeSpan.FromMinutes(5));
        }

        private static void SeedTmdbSeriesSearchResults(TmdbApi tmdbApi, string name, string language, params SearchTv[] results)
        {
            GetTmdbMemoryCache(tmdbApi).Set(
                $"searchseries-{name}-{language}",
                new SearchContainer<SearchTv>
                {
                    Results = results.ToList(),
                },
                TimeSpan.FromMinutes(5));
        }

        private static void SeedTmdbPerson(TmdbApi tmdbApi, int tmdbId, string? name, string? language = null, string? countryCode = null)
        {
            GetTmdbMemoryCache(tmdbApi).Set(
                GetTmdbPersonCacheKey(tmdbId, language ?? string.Empty, countryCode),
                new TMDbLib.Objects.People.Person
                {
                    Id = tmdbId,
                    Name = name,
                },
                TimeSpan.FromMinutes(5));
        }

        private static void SetTmdbSeriesCredits(
            TvShow series,
            IEnumerable<Dictionary<string, object?>> castEntries,
            IEnumerable<Dictionary<string, object?>> crewEntries)
        {
            var creditsProperty = typeof(TvShow).GetProperty("Credits");
            Assert.IsNotNull(creditsProperty, "TMDb tv Credits 属性未定义");
            var credits = Activator.CreateInstance(creditsProperty!.PropertyType);
            Assert.IsNotNull(credits, "无法创建 TMDb tv Credits 实例");

            SetTmdbCreditList(credits!, "Cast", castEntries);
            SetTmdbCreditList(credits!, "Crew", crewEntries);
            creditsProperty.SetValue(series, credits);
        }

        private static void SetTmdbCreditList(object credits, string propertyName, IEnumerable<Dictionary<string, object?>> entries)
        {
            var listProperty = credits.GetType().GetProperty(propertyName);
            Assert.IsNotNull(listProperty, $"TMDb Credits.{propertyName} 属性未定义");
            var itemType = listProperty!.PropertyType.GetGenericArguments().Single();
            var listType = typeof(List<>).MakeGenericType(itemType);
            var list = Activator.CreateInstance(listType) as IList;
            Assert.IsNotNull(list, $"无法创建 TMDb Credits.{propertyName} 列表实例");

            foreach (var entry in entries)
            {
                var item = Activator.CreateInstance(itemType);
                Assert.IsNotNull(item, $"无法创建 TMDb Credits.{propertyName} 条目实例");
                foreach (var pair in entry)
                {
                    var property = itemType.GetProperty(pair.Key);
                    Assert.IsNotNull(property, $"TMDb credit 条目缺少属性 {pair.Key}");
                    property!.SetValue(item, pair.Value);
                }

                list!.Add(item);
            }

            listProperty.SetValue(credits, list);
        }

        private static Dictionary<string, object?> CreateCastCredit(int id, string name, string character, int order)
        {
            return new Dictionary<string, object?>
            {
                ["Id"] = id,
                ["Name"] = name,
                ["Character"] = character,
                ["Order"] = order,
            };
        }

        private static string GetTmdbPersonCacheKey(int tmdbId, string language, string? countryCode)
        {
            var cacheKeyMethod = typeof(TmdbApi).GetMethod("GetPersonCacheKey", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(cacheKeyMethod, "TmdbApi.GetPersonCacheKey 未定义");
            var cacheKey = cacheKeyMethod!.Invoke(null, new object?[] { tmdbId, language, countryCode }) as string;
            Assert.IsFalse(string.IsNullOrEmpty(cacheKey), "TmdbApi.GetPersonCacheKey 返回了无效缓存键");
            return cacheKey!;
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

        private sealed class LlmAuthoritativeTrackingSeries : Series
        {
            private List<PersonInfo> simulatedPeople = new List<PersonInfo>();

            public void SetSimulatedPeople(IEnumerable<PersonInfo> people)
            {
                this.simulatedPeople = people.ToList();
            }

            private System.Collections.IEnumerable GetPeople()
            {
                return this.simulatedPeople;
            }
        }
    }
}
