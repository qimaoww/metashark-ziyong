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
        public async Task GetMetadata_TextCompletionDisabled_DoesNotCallMetadataAssistButAllowsExternalIdResolver()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(allowTextCompletion: false));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 8872, "zh-CN", CreateTmdbSeries(8872, "关闭文本补全剧集", "关闭文本补全简介", null, null));
            var llmService = CreateLlmService("不应调用的文本补全", 2024);
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueResult(CreateExternalIdResolutionResult("TMDb", "8872", "Series", MetadataProvider.Tmdb.ToString()));
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor("text-disabled"),
                tmdbApi: tmdbApi,
                llmService: llmService,
                llmExternalIdResolutionService: externalIdService);
            var info = CreateSeriesInfo("关闭文本补全剧集", "/mnt/media/TV/关闭文本补全剧集");

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, llmService.Requests.Count);
            Assert.AreEqual(1, externalIdService.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("8872", result.Item!.GetProviderId(MetadataProvider.Tmdb));
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
                LlmProviderFlowTestHelpers.CreateExplicitRefreshContextAccessor(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture), replaceAllMetadata: true),
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
        public async Task GetMetadata_DefaultOverwriteRefreshWithDoubanPrimary_PreservesCompletedTmdbId()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableEpisodeGroupMappingAssist: true, enableTmdbCorrection: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var doubanSubject = CreateDoubanSubject("overwrite-douban", "覆盖保留豆瓣剧集", 2024);
            SeedDoubanSubject(doubanApi, doubanSubject);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 8870, "zh-CN", CreateTmdbSeries(8870, "覆盖保留 TMDb 剧集", "覆盖保留 TMDb 简介", "tt8870000", "887000"));
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueExistingProviderDecision(LlmAssistTriggerDecision.Rejected("ExistingProviderIdsConsistent"));
            externalIdService.EnqueueResult(CreateExternalIdResolutionResult("TMDb", "9999", "Series", MetadataProvider.Tmdb.ToString()));
            var llmApi = new RecordingLlmApi("overwrite-group", 0.95);
            var refreshCalls = new List<QueueRefreshCall>();
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateExplicitRefreshContextAccessor(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture), replaceAllMetadata: true),
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmEpisodeGroupMappingProviderAssistService: CreateEpisodeGroupMappingProviderAssistService(loggerFactory, llmApi, tmdbApi, Array.Empty<BaseItem>(), refreshCalls),
                llmExternalIdResolutionService: externalIdService);
            var info = CreateSeriesInfo("覆盖保留豆瓣剧集", "/mnt/media/TV/覆盖保留豆瓣剧集");
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "overwrite-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_overwrite-douban",
                [MetadataProvider.Tmdb.ToString()] = "8870",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("覆盖保留豆瓣剧集", result.Item!.Name);
            Assert.AreEqual("覆盖保留豆瓣剧集简介", result.Item.Overview);
            Assert.AreEqual("overwrite-douban", result.Item.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Douban_overwrite-douban", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual("8870", result.Item.GetProviderId(MetadataProvider.Tmdb), "覆盖刷新不能丢掉已经由 LLM 补齐的 TMDbID。");
            Assert.AreEqual("8870", info.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual(1, externalIdService.ExistingProviderDecisionRequests.Count, "覆盖刷新可以检查已有 ID 是否冲突，但一致时必须停在默认 Douban 链路。");
            Assert.AreEqual(0, externalIdService.Requests.Count, "覆盖刷新已有 TMDbID 时不应再次走普通 external-id 补全。");
            Assert.AreEqual(0, externalIdService.CorrectionRequests.Count, "已有 Douban 主链加 TMDbID 不应被当成纠错。");
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
            var persistenceService = new RecordingPersistenceService(TmdbEpisodeGroupMapPersistenceResult.SavedResult(string.Empty, "8840=candidate-group"));
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(mappedSeries.Id.ToString("N", CultureInfo.InvariantCulture)),
                libraryManager: CreateLibraryManager(new[] { mappedSeries }).Object,
                tmdbApi: tmdbApi,
                llmEpisodeGroupMappingProviderAssistService: CreateEpisodeGroupMappingProviderAssistService(loggerFactory, llmApi, tmdbApi, new[] { mappedSeries }, refreshCalls, persistenceService));
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
            Assert.AreEqual(1, persistenceService.Calls.Count);
            AssertQueuedSeries(refreshCalls, mappedSeries.Id);
            LlmProviderFlowTestHelpers.AssertNoSensitiveContent(llmApi.Prompts.Single());
            StringAssert.Contains(llmApi.Prompts.Single(), "seriesTmdbId");
            StringAssert.Contains(llmApi.Prompts.Single(), "candidate-group");
        }

        [TestMethod]
        public async Task GetMetadata_ManualTmdbMatch_WhenPersistenceReturnsNoChange_ShouldNotQueueRefresh()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableEpisodeGroupMappingAssist: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            var tvShow = CreateTmdbSeries(8841, "映射剧集无变化", "映射简介", null, null);
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
            SeedTmdbSeries(tmdbApi, 8841, "zh-CN", tvShow);
            ExplicitEpisodeGroupMappingTestHelper.SeedEpisodeGroupById(tmdbApi, "candidate-group", "zh-CN");
            var llmApi = new RecordingLlmApi("candidate-group", 0.95);
            var mappedSeries = CreateSeries(Guid.NewGuid(), "映射剧集无变化", "8841");
            var refreshCalls = new List<QueueRefreshCall>();
            var persistenceService = new RecordingPersistenceService(TmdbEpisodeGroupMapPersistenceResult.NoChange(string.Empty));
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(mappedSeries.Id.ToString("N", CultureInfo.InvariantCulture)),
                libraryManager: CreateLibraryManager(new[] { mappedSeries }).Object,
                tmdbApi: tmdbApi,
                llmEpisodeGroupMappingProviderAssistService: CreateEpisodeGroupMappingProviderAssistService(loggerFactory, llmApi, tmdbApi, new[] { mappedSeries }, refreshCalls, persistenceService));
            var info = CreateSeriesInfo("映射剧集无变化", "/mnt/media/TV/映射剧集无变化");
            info.ProviderIds = new Dictionary<string, string>
            {
                [MetaSharkPlugin.ProviderId] = "Tmdb_8841",
                [MetadataProvider.Tmdb.ToString()] = "8841",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual(1, llmApi.Prompts.Count);
            Assert.AreEqual(0, refreshCalls.Count);
            Assert.AreEqual(1, persistenceService.Calls.Count);
        }

        [TestMethod]
        public async Task GetMetadata_ManualTmdbMatch_WhenPersistenceReturnsConflict_ShouldNotQueueRefresh()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableEpisodeGroupMappingAssist: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            var tvShow = CreateTmdbSeries(8842, "映射剧集冲突", "映射简介", null, null);
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
            SeedTmdbSeries(tmdbApi, 8842, "zh-CN", tvShow);
            ExplicitEpisodeGroupMappingTestHelper.SeedEpisodeGroupById(tmdbApi, "candidate-group", "zh-CN");
            var llmApi = new RecordingLlmApi("candidate-group", 0.95);
            var mappedSeries = CreateSeries(Guid.NewGuid(), "映射剧集冲突", "8842");
            var refreshCalls = new List<QueueRefreshCall>();
            var persistenceService = new RecordingPersistenceService(TmdbEpisodeGroupMapPersistenceResult.Conflict(string.Empty, "8842=other-group"));
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(mappedSeries.Id.ToString("N", CultureInfo.InvariantCulture)),
                libraryManager: CreateLibraryManager(new[] { mappedSeries }).Object,
                tmdbApi: tmdbApi,
                llmEpisodeGroupMappingProviderAssistService: CreateEpisodeGroupMappingProviderAssistService(loggerFactory, llmApi, tmdbApi, new[] { mappedSeries }, refreshCalls, persistenceService));
            var info = CreateSeriesInfo("映射剧集冲突", "/mnt/media/TV/映射剧集冲突");
            info.ProviderIds = new Dictionary<string, string>
            {
                [MetaSharkPlugin.ProviderId] = "Tmdb_8842",
                [MetadataProvider.Tmdb.ToString()] = "8842",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual(1, llmApi.Prompts.Count);
            Assert.AreEqual(0, refreshCalls.Count);
            Assert.AreEqual(1, persistenceService.Calls.Count);
        }

        [TestMethod]
        public async Task GetMetadata_ManualTmdbMatch_WhenPersistenceSaveFails_ShouldNotQueueRefresh()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableEpisodeGroupMappingAssist: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            var tvShow = CreateTmdbSeries(8843, "映射剧集保存失败", "映射简介", null, null);
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
            SeedTmdbSeries(tmdbApi, 8843, "zh-CN", tvShow);
            ExplicitEpisodeGroupMappingTestHelper.SeedEpisodeGroupById(tmdbApi, "candidate-group", "zh-CN");
            var llmApi = new RecordingLlmApi("candidate-group", 0.95);
            var mappedSeries = CreateSeries(Guid.NewGuid(), "映射剧集保存失败", "8843");
            var refreshCalls = new List<QueueRefreshCall>();
            var persistenceService = new RecordingPersistenceService(TmdbEpisodeGroupMapPersistenceResult.Failed("SaveConfigurationFailed", string.Empty, string.Empty, null));
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(mappedSeries.Id.ToString("N", CultureInfo.InvariantCulture)),
                libraryManager: CreateLibraryManager(new[] { mappedSeries }).Object,
                tmdbApi: tmdbApi,
                llmEpisodeGroupMappingProviderAssistService: CreateEpisodeGroupMappingProviderAssistService(loggerFactory, llmApi, tmdbApi, new[] { mappedSeries }, refreshCalls, persistenceService));
            var info = CreateSeriesInfo("映射剧集保存失败", "/mnt/media/TV/映射剧集保存失败");
            info.ProviderIds = new Dictionary<string, string>
            {
                [MetaSharkPlugin.ProviderId] = "Tmdb_8843",
                [MetadataProvider.Tmdb.ToString()] = "8843",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual(1, llmApi.Prompts.Count);
            Assert.AreEqual(0, refreshCalls.Count);
            Assert.AreEqual(1, persistenceService.Calls.Count);
        }

        [TestMethod]
        public async Task GetMetadata_ManualTmdbMatch_WhenSameMappingReenters_ShouldQueueRefreshOnlyOnce()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableEpisodeGroupMappingAssist: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            var tvShow = CreateTmdbSeries(8844, "重复映射剧集", "映射简介", null, null);
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
            SeedTmdbSeries(tmdbApi, 8844, "zh-CN", tvShow);
            ExplicitEpisodeGroupMappingTestHelper.SeedEpisodeGroupById(tmdbApi, "candidate-group", "zh-CN");
            var llmApi = new RecordingLlmApi("candidate-group", 0.95);
            var mappedSeries = CreateSeries(Guid.NewGuid(), "重复映射剧集", "8844");
            var refreshCalls = new List<QueueRefreshCall>();
            var persistenceService = new SequencePersistenceService(
                TmdbEpisodeGroupMapPersistenceResult.SavedResult(string.Empty, "8844=candidate-group"),
                TmdbEpisodeGroupMapPersistenceResult.NoChange("8844=candidate-group"));
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(mappedSeries.Id.ToString("N", CultureInfo.InvariantCulture)),
                libraryManager: CreateLibraryManager(new[] { mappedSeries }).Object,
                tmdbApi: tmdbApi,
                llmEpisodeGroupMappingProviderAssistService: CreateEpisodeGroupMappingProviderAssistService(loggerFactory, llmApi, tmdbApi, new[] { mappedSeries }, refreshCalls, persistenceService));
            var info = CreateSeriesInfo("重复映射剧集", "/mnt/media/TV/重复映射剧集");
            info.ProviderIds = new Dictionary<string, string>
            {
                [MetaSharkPlugin.ProviderId] = "Tmdb_8844",
                [MetadataProvider.Tmdb.ToString()] = "8844",
            };

            var firstResult = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);
            var secondResult = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(firstResult.HasMetadata);
            Assert.IsTrue(secondResult.HasMetadata);
            Assert.AreEqual(2, llmApi.Prompts.Count);
            Assert.AreEqual(1, refreshCalls.Count);
            AssertQueuedSeries(refreshCalls, mappedSeries.Id);
            Assert.AreEqual(1, persistenceService.Calls.Count);
        }

        [TestMethod]
        public async Task GetMetadata_UserRefreshAfterAutoQueuedRefresh_ShouldSuppressLoopAndNotRequeue()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableEpisodeGroupMappingAssist: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            var tvShow = CreateTmdbSeries(8845, "回流映射剧集", "映射简介", null, null);
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
            SeedTmdbSeries(tmdbApi, 8845, "zh-CN", tvShow);
            ExplicitEpisodeGroupMappingTestHelper.SeedEpisodeGroupById(tmdbApi, "candidate-group", "zh-CN");
            var llmApi = new RecordingLlmApi("candidate-group", 0.95);
            var mappedSeries = CreateSeries(Guid.NewGuid(), "回流映射剧集", "8845");
            var refreshCalls = new List<QueueRefreshCall>();
            var persistenceService = new SequencePersistenceService(TmdbEpisodeGroupMapPersistenceResult.SavedResult(string.Empty, "8845=candidate-group"));
            var manualProvider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(mappedSeries.Id.ToString("N", CultureInfo.InvariantCulture)),
                libraryManager: CreateLibraryManager(new[] { mappedSeries }).Object,
                tmdbApi: tmdbApi,
                llmEpisodeGroupMappingProviderAssistService: CreateEpisodeGroupMappingProviderAssistService(loggerFactory, llmApi, tmdbApi, new[] { mappedSeries }, refreshCalls, persistenceService));
            var info = CreateSeriesInfo("回流映射剧集", "/mnt/media/TV/回流映射剧集");
            info.ProviderIds = new Dictionary<string, string>
            {
                [MetaSharkPlugin.ProviderId] = "Tmdb_8845",
                [MetadataProvider.Tmdb.ToString()] = "8845",
            };

            var manualResult = await manualProvider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(manualResult.HasMetadata);
            Assert.AreEqual(1, llmApi.Prompts.Count);
            Assert.AreEqual(1, refreshCalls.Count);

            var refreshProvider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateExplicitRefreshContextAccessor(mappedSeries.Id.ToString("D", CultureInfo.InvariantCulture)),
                libraryManager: CreateLibraryManager(new[] { mappedSeries }).Object,
                tmdbApi: tmdbApi,
                llmEpisodeGroupMappingProviderAssistService: CreateEpisodeGroupMappingProviderAssistService(loggerFactory, llmApi, tmdbApi, new[] { mappedSeries }, refreshCalls, persistenceService));

            var refreshResult = await refreshProvider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(refreshResult.HasMetadata);
            Assert.AreEqual(1, llmApi.Prompts.Count, "回流 refresh 应在 provider assist 入口被抑制，不再调用 LLM。");
            Assert.AreEqual(1, refreshCalls.Count, "回流 refresh 不应再次排队 refresh。");
            Assert.AreEqual(1, persistenceService.Calls.Count, "回流 refresh 不应再次进入保存。");
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
        public async Task GetMetadata_DoubanMetadataWithoutTmdbId_CompletesTmdbIdAndKeepsDoubanMetadata()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableEpisodeGroupMappingAssist: true, enableTmdbCorrection: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var doubanSubject = CreateDoubanSubject("1291843", "豆瓣已有剧集", 2024);
            SeedDoubanSubject(doubanApi, doubanSubject);
            var tmdbApi = new TmdbApi(loggerFactory);
            var tvShow = CreateTmdbSeries(8860, "TMDb 补充剧集", "TMDb 补充简介", "tt8860000", "886000");
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
            var persistenceService = new RecordingLlmTmdbCorrectionMapPersistenceService();
            var stages = new List<string>();
            SeriesProvider.TestTraceSink = trace => stages.Add(trace.Stage);
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(mappedSeries.Id.ToString("N", CultureInfo.InvariantCulture)),
                libraryManager: CreateLibraryManager(new[] { mappedSeries }).Object,
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmEpisodeGroupMappingProviderAssistService: CreateEpisodeGroupMappingProviderAssistService(loggerFactory, llmApi, tmdbApi, new[] { mappedSeries }, refreshCalls),
                llmExternalIdResolutionService: externalIdService,
                llmTmdbCorrectionMapPersistenceService: persistenceService);
            var info = CreateSeriesInfo("豆瓣已有剧集", "/mnt/media/TV/豆瓣已有剧集");
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "1291843",
                [MetaSharkPlugin.ProviderId] = "Douban_1291843",
            };

            try
            {
                var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual(1, externalIdService.Requests.Count, "已有 Douban metadata 但缺 TMDb 时仍应尝试外部 ID 解析。 ");
                Assert.AreEqual(0, externalIdService.CorrectionRequests.Count, "普通缺失 TMDbID 补全不应进入 TMDb 纠错链路。");
                Assert.IsTrue(stages.Contains("BeforeDoubanMetadata"), "LLM 补齐 TMDbID 后，本轮仍应继续默认 Douban 元数据分支。");
                Assert.IsFalse(stages.Contains("BeforeTmdbMetadata"), "普通缺失 TMDbID 补全不应强制切换到 TMDb 元数据分支。");
                Assert.IsTrue(result.HasMetadata);
                Assert.AreEqual("豆瓣已有剧集", result.Item!.Name);
                Assert.AreEqual("豆瓣已有剧集简介", result.Item.Overview);
                Assert.AreEqual("1291843", result.Item.GetProviderId(BaseProvider.DoubanProviderId));
                Assert.AreEqual("Douban_1291843", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
                Assert.AreEqual("8860", result.Item.GetProviderId(MetadataProvider.Tmdb));
                Assert.AreEqual("8860", info.GetProviderId(MetadataProvider.Tmdb));
                Assert.AreEqual("tt8860000", result.Item.GetProviderId(MetadataProvider.Imdb));
                Assert.AreEqual("886000", result.Item.GetProviderId(MetadataProvider.Tvdb));
                Assert.AreEqual(1, persistenceService.CompletionCalls.Count, "普通缺失 TMDbID 补全应持久化补全来源，供后续刷新保留 TMDbID。");
                Assert.AreEqual("series:douban:1291843=tmdb:8860", MetaSharkPlugin.Instance!.Configuration.LlmTmdbCompletionMap);
                Assert.AreEqual(0, persistenceService.CorrectionCalls.Count, "普通缺失 TMDbID 补全不应持久化 Douban -> TMDb 纠错映射。");
                Assert.AreEqual(string.Empty, MetaSharkPlugin.Instance!.Configuration.LlmTmdbCorrectionMap);
                Assert.AreEqual(1, llmApi.Prompts.Count, "剧集组映射只能在已验证 TMDbId 写入后运行。 ");
                AssertQueuedSeries(refreshCalls, mappedSeries.Id);
            }
            finally
            {
                SeriesProvider.TestTraceSink = null;
            }
        }

        [TestMethod]
        public async Task GetMetadata_TmdbCompletionMapWithDefaultScraperMode_KeepsDoubanMetadataAndDoesNotInvokeCorrection()
        {
            EnsurePluginInstance();
            var configuration = CreateLlmConfiguration(enableTmdbCorrection: true);
            configuration.LlmTmdbCompletionMap = "series:douban:37291769=tmdb:251782";
            ReplacePluginConfiguration(configuration);
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var doubanSubject = CreateDoubanSubject("37291769", "灰原君的青春二周目", 2026);
            SeedDoubanSubject(doubanApi, doubanSubject);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 251782, "zh-CN", CreateTmdbSeries(251782, "Haibara-kun's New Game Plus", "TMDb overview", "tt2517820", "251782"));
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueExistingProviderDecision(LlmAssistTriggerDecision.Allowed("StaleExternalIdConflict"));
            externalIdService.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.VerifiedExistingTmdb("251782", "ExistingTmdbVerified"));
            var stages = new List<string>();
            SeriesProvider.TestTraceSink = trace => stages.Add(trace.Stage);
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateExplicitRefreshContextAccessor(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture), replaceAllMetadata: true),
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdService);
            var info = CreateSeriesInfo("灰原君的青春二周目", "/dongman/动画/灰原同学重返过去，开启所向无敌的第二轮青春游戏 (2026)");
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "37291769",
                [MetaSharkPlugin.ProviderId] = "Douban_37291769",
                [MetadataProvider.Tmdb.ToString()] = "251782",
            };

            try
            {
                var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

                Assert.IsTrue(result.HasMetadata);
                Assert.AreEqual("灰原君的青春二周目", result.Item!.Name);
                Assert.AreEqual("灰原君的青春二周目简介", result.Item.Overview);
                Assert.AreEqual("37291769", result.Item.GetProviderId(BaseProvider.DoubanProviderId));
                Assert.AreEqual("Douban_37291769", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
                Assert.AreEqual("251782", result.Item.GetProviderId(MetadataProvider.Tmdb));
                Assert.AreEqual(0, externalIdService.CorrectionRequests.Count, "普通补全来源命中后不应升级成 TMDb 纠错链路。");
                Assert.IsTrue(stages.Contains("BeforeDoubanMetadata"), "补全来源命中后仍应走默认 Douban 元数据分支。");
                Assert.IsFalse(stages.Contains("BeforeTmdbMetadata"), "补全来源命中后不应强制切到 TMDb 元数据分支。");
                Assert.AreEqual("series:douban:37291769=tmdb:251782", MetaSharkPlugin.Instance!.Configuration.LlmTmdbCompletionMap);
                Assert.AreEqual(string.Empty, MetaSharkPlugin.Instance.Configuration.LlmTmdbCorrectionMap);
            }
            finally
            {
                SeriesProvider.TestTraceSink = null;
            }
        }

        [TestMethod]
        public async Task GetMetadata_StaleExternalIdConflict_WhenDoubanMetadataIsSemanticallyConsistent_DoesNotCallExternalIdResolver()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableEpisodeGroupMappingAssist: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var doubanSubject = CreateDoubanSubject("consistent-series-douban", "一致剧集", 2024);
            SeedDoubanSubject(doubanApi, doubanSubject);
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueExistingProviderDecision(LlmAssistTriggerDecision.Rejected("ExistingProviderIdsConsistent"));
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateExplicitSearchMissingContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                llmExternalIdResolutionService: externalIdService);
            var info = CreateSeriesInfo("一致剧集", "/mnt/media/TV/一致剧集");
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "consistent-series-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_consistent-series-douban",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, externalIdService.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("consistent-series-douban", result.Item!.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.Item.GetProviderId(MetadataProvider.Tmdb)));
        }

        [TestMethod]
        public async Task GetMetadata_StaleExternalIdConflict_WhenDoubanMetadataIsSemanticallyStale_StillPreservesOriginalIdsWithoutResolverWrite()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableEpisodeGroupMappingAssist: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var doubanSubject = CreateDoubanSubject("stale-series-douban", "错误旧剧集", 1999);
            SeedDoubanSubject(doubanApi, doubanSubject);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 8866, "zh-CN", CreateTmdbSeries(8866, "修正后剧集", "修正后简介", "tt8866000", "886600"));
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueExistingProviderDecision(LlmAssistTriggerDecision.Allowed("StaleExternalIdConflict"));
            externalIdService.EnqueueResult(CreateExternalIdResolutionResult("TMDb", "8866", "Series", MetadataProvider.Tmdb.ToString()));
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateExplicitSearchMissingContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdService);
            var info = CreateSeriesInfo("修正后剧集", "/mnt/media/TV/修正后剧集");
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "stale-series-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_stale-series-douban",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("stale-series-douban", result.Item!.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.Item.GetProviderId(MetadataProvider.Tmdb)) || result.Item.GetProviderId(MetadataProvider.Tmdb) == "8866");
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
        public async Task GetMetadata_ExistingTmdbIdCorrectionSwitchOff_DoesNotInvokeExternalIdResolverOrOverwrite()
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
            Assert.AreEqual(0, externalIdService.CorrectionRequests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("8862", info.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("8862", result.Item!.GetProviderId(MetadataProvider.Tmdb));
        }

        [TestMethod]
        public async Task GetMetadata_ExistingTmdbIdCorrectionDisabled_DoesNotInvokeCorrectionOrOverwrite()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableTmdbCorrection: false));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var doubanSubject = CreateDoubanSubject("correction-off-series-douban", "纠错关闭剧集", 2024);
            SeedDoubanSubject(doubanApi, doubanSubject);
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueExistingProviderDecision(LlmAssistTriggerDecision.Rejected("ExistingProviderIdsConsistent"));
            externalIdService.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.Verified("222", "test verified replacement"));
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor("correction-off-series"),
                doubanApi: doubanApi,
                llmExternalIdResolutionService: externalIdService);
            var info = CreateSeriesInfo("纠错关闭剧集", "/mnt/media/TV/纠错关闭剧集");
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "correction-off-series-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_correction-off-series-douban",
                [MetadataProvider.Tmdb.ToString()] = "111",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, externalIdService.CorrectionRequests.Count);
            Assert.AreEqual(0, externalIdService.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("111", result.Item!.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("correction-off-series-douban", result.Item.GetProviderId(BaseProvider.DoubanProviderId));
        }

        [TestMethod]
        public async Task GetMetadata_TmdbCorrection_ManualRefreshCorrectionVerified_UsesTmdbMetadataAndClearsStaleDouban()
        {
            EnsurePluginInstance();
            var configuration = CreateLlmConfiguration(enableTmdbCorrection: true);
            ReplacePluginConfiguration(configuration);
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var doubanSubject = CreateDoubanSubject("manual-correction-series-douban", "手动纠错剧集", 2024);
            SeedDoubanSubject(doubanApi, doubanSubject);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 222, "zh-CN", CreateTmdbSeries(222, "手动纠错后剧集", "纠错后简介", "tt222", "tvdb222"));
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.Verified("222", "test verified replacement"));
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateExplicitRefreshContextAccessor(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdService);
            var info = CreateSeriesInfo("手动纠错剧集", "/mnt/media/TV/手动纠错剧集");
            info.IsAutomated = false;
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "manual-correction-series-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_manual-correction-series-douban",
                [MetadataProvider.Tmdb.ToString()] = "111",
                [MetadataProvider.Imdb.ToString()] = "ttold",
                [MetadataProvider.Tvdb.ToString()] = "tvdbold",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdService.ExistingProviderDecisionRequests.Count);
            Assert.AreEqual(1, externalIdService.CorrectionRequests.Count);
            Assert.AreEqual(0, externalIdService.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("手动纠错后剧集", result.Item!.Name);
            Assert.AreEqual("纠错后简介", result.Item.Overview);
            Assert.AreEqual("222", info.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("222", result.Item!.GetProviderId(MetadataProvider.Tmdb));
            Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Tmdb_222", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual("ttold", result.Item.GetProviderId(MetadataProvider.Imdb));
            Assert.AreEqual("tvdbold", result.Item.GetProviderId(MetadataProvider.Tvdb));
            Assert.AreEqual("111", externalIdService.CorrectionRequests[0].OldTmdbId);
            Assert.AreEqual(DefaultScraperSemantic.UserRefresh, externalIdService.CorrectionRequests[0].Semantic);
        }

        [TestMethod]
        public async Task GetMetadata_TmdbCorrection_VerifiedExistingTmdbUsesTmdbMetadataInsteadOfStaleDouban()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableTmdbCorrection: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var staleDoubanSubject = CreateDoubanSubject("26862290", "Re：从零开始的休息时间", 2016);
            SeedDoubanSubject(doubanApi, staleDoubanSubject);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 65942, "zh-CN", CreateTmdbSeries(65942, "Re：从零开始的异世界生活", "主线简介", "tt5705718", "305089"));
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueExistingProviderDecision(LlmAssistTriggerDecision.Allowed("ExplicitSearchMissingMetadataRefresh"));
            externalIdService.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.VerifiedExistingTmdb("65942", "ExistingTmdbVerified"));
            var infoPath = "/mnt/media/TV/Re：从零开始的异世界生活 (2016)";
            var currentSeries = new TrackingSeries
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
                    [MetadataProvider.Imdb.ToString()] = "tt5705718",
                    [MetadataProvider.Tvdb.ToString()] = "305089",
                },
            };
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.FindByPath(infoPath, true)).Returns(currentSeries);
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateExplicitRefreshContextAccessor(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)),
                libraryManager: libraryManager.Object,
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdService);
            var info = CreateSeriesInfo("Re：从零开始的休息时间", infoPath);
            info.Year = 2016;
            info.IsAutomated = false;
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "26862290",
                [MetaSharkPlugin.ProviderId] = "Douban_26862290",
                [MetadataProvider.Tmdb.ToString()] = "65942",
                [MetadataProvider.Imdb.ToString()] = "tt5705718",
                [MetadataProvider.Tvdb.ToString()] = "305089",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdService.ExistingProviderDecisionRequests.Count);
            Assert.AreEqual(1, externalIdService.CorrectionRequests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("Re：从零开始的异世界生活", result.Item!.Name);
            Assert.AreEqual("主线简介", result.Item.Overview);
            Assert.AreEqual("65942", result.Item.GetProviderId(MetadataProvider.Tmdb));
            Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Tmdb_65942", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual("tt5705718", result.Item.GetProviderId(MetadataProvider.Imdb));
            Assert.AreEqual("305089", result.Item.GetProviderId(MetadataProvider.Tvdb));
            Assert.AreEqual("Re：从零开始的异世界生活", currentSeries.Name);
            Assert.AreEqual("主线简介", currentSeries.Overview);
            Assert.IsFalse(currentSeries.ProviderIds.ContainsKey(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Tmdb_65942", currentSeries.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual(1, currentSeries.UpdateToRepositoryCallCount);
        }

        [TestMethod]
        public async Task GetMetadata_TmdbCorrection_VerifiedReplacementUsesTmdbMetadataAndClearsStaleDouban()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableTmdbCorrection: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var staleDoubanSubject = CreateDoubanSubject("replacement-series-douban", "错误豆瓣剧集", 2024);
            SeedDoubanSubject(doubanApi, staleDoubanSubject);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 222, "zh-CN", CreateTmdbSeries(222, "正确 TMDb 剧集", "正确 TMDb 简介", "tt222", "tvdb222"));
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueExistingProviderDecision(LlmAssistTriggerDecision.Allowed("StaleExternalIdConflict"));
            externalIdService.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.Verified("222", "test verified replacement"));
            var infoPath = "/mnt/media/TV/正确 TMDb 剧集";
            var currentSeries = new TrackingSeries
            {
                Id = Guid.NewGuid(),
                Name = "错误豆瓣剧集",
                Path = infoPath,
                ProviderIds = new Dictionary<string, string>
                {
                    [BaseProvider.DoubanProviderId] = "replacement-series-douban",
                    [MetaSharkPlugin.ProviderId] = "Douban_replacement-series-douban",
                    [MetadataProvider.Tmdb.ToString()] = "111",
                    [MetadataProvider.Imdb.ToString()] = "ttold",
                    [MetadataProvider.Tvdb.ToString()] = "tvdbold",
                },
            };
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.FindByPath(infoPath, true)).Returns(currentSeries);
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateExplicitRefreshContextAccessor(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)),
                libraryManager: libraryManager.Object,
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdService);
            var info = CreateSeriesInfo("错误豆瓣剧集", infoPath);
            info.Year = 2024;
            info.IsAutomated = false;
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "replacement-series-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_replacement-series-douban",
                [MetadataProvider.Tmdb.ToString()] = "111",
                [MetadataProvider.Imdb.ToString()] = "ttold",
                [MetadataProvider.Tvdb.ToString()] = "tvdbold",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdService.ExistingProviderDecisionRequests.Count);
            Assert.AreEqual(1, externalIdService.CorrectionRequests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("正确 TMDb 剧集", result.Item!.Name);
            Assert.AreEqual("正确 TMDb 简介", result.Item.Overview);
            Assert.AreEqual("222", info.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("222", result.Item.GetProviderId(MetadataProvider.Tmdb));
            Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Tmdb_222", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual("ttold", result.Item.GetProviderId(MetadataProvider.Imdb));
            Assert.AreEqual("tvdbold", result.Item.GetProviderId(MetadataProvider.Tvdb));
            Assert.IsFalse(currentSeries.ProviderIds.ContainsKey(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Tmdb_222", currentSeries.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual("222", currentSeries.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual(1, currentSeries.UpdateToRepositoryCallCount);
        }

        [TestMethod]
        public async Task GetMetadata_TmdbCorrectionPersistenceFailure_StillUsesVerifiedTmdbMetadataForCurrentRefresh()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableTmdbCorrection: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var staleDoubanSubject = CreateDoubanSubject("26862290", "Re：从零开始的休息时间", 2016);
            SeedDoubanSubject(doubanApi, staleDoubanSubject);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 65942, "zh-CN", CreateTmdbSeries(65942, "Re：从零开始的异世界生活", "主线简介", "tt5705718", "305089"));
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueExistingProviderDecision(LlmAssistTriggerDecision.Allowed("StaleExternalIdConflict"));
            externalIdService.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.Verified("65942", "test verified replacement"));
            var persistenceService = new Mock<ILlmTmdbCorrectionMapPersistenceService>();
            persistenceService
                .Setup(x => x.TryUpsertDoubanCorrectionAsync(nameof(Series), "26862290", "65942", It.IsAny<CancellationToken>()))
                .ReturnsAsync(LlmTmdbCorrectionMapPersistenceResult.Failed("SaveConfigurationFailed", string.Empty, string.Empty, null));
            var infoPath = "/mnt/media/TV/Re：从零开始的异世界生活 (2016)";
            var currentSeries = new TrackingSeries
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
                    [MetadataProvider.Imdb.ToString()] = "tt5705718",
                    [MetadataProvider.Tvdb.ToString()] = "305089",
                },
            };
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.FindByPath(infoPath, true)).Returns(currentSeries);
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateExplicitRefreshContextAccessor(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)),
                libraryManager: libraryManager.Object,
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdService,
                llmTmdbCorrectionMapPersistenceService: persistenceService.Object);
            var info = CreateSeriesInfo("Re：从零开始的休息时间", infoPath);
            info.Year = 2016;
            info.IsAutomated = false;
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "26862290",
                [MetaSharkPlugin.ProviderId] = "Douban_26862290",
                [MetadataProvider.Tmdb.ToString()] = "65942",
                [MetadataProvider.Imdb.ToString()] = "tt5705718",
                [MetadataProvider.Tvdb.ToString()] = "305089",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdService.ExistingProviderDecisionRequests.Count);
            Assert.AreEqual(1, externalIdService.CorrectionRequests.Count);
            persistenceService.Verify(x => x.TryUpsertDoubanCorrectionAsync(nameof(Series), "26862290", "65942", It.IsAny<CancellationToken>()), Times.Once);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("Re：从零开始的异世界生活", result.Item!.Name);
            Assert.AreEqual("主线简介", result.Item.Overview);
            Assert.AreEqual("65942", result.Item.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("Tmdb_65942", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId));
        }

        [TestMethod]
        public async Task GetMetadata_TmdbCorrection_BridgedFollowUpAfterPersistedMapKeepsUsingVerifiedTmdbMetadataWithoutRequeryingCorrection()
        {
            EnsurePluginInstance();
            var configuration = CreateLlmConfiguration(enableTmdbCorrection: true);
            ReplacePluginConfiguration(configuration);
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var staleDoubanSubject = CreateDoubanSubject("26862290", "Re：从零开始的休息时间", 2016);
            SeedDoubanSubject(doubanApi, staleDoubanSubject);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 65942, "zh-CN", CreateTmdbSeries(65942, "Re：从零开始的异世界生活", "主线简介", "tt5705718", "305089"));
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueExistingProviderDecision(LlmAssistTriggerDecision.Allowed("StaleExternalIdConflict"));
            externalIdService.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.VerifiedExistingTmdb("65942", "ExistingTmdbVerified"));
            var infoPath = "/mnt/media/TV/Re：从零开始的异世界生活 (2016)";
            var currentSeries = new TrackingSeries
            {
                Id = Guid.NewGuid(),
                Name = "Re：从零开始的休息时间",
                Path = infoPath,
                ProviderIds = new Dictionary<string, string>
                {
                    [BaseProvider.DoubanProviderId] = "26862290",
                    [MetaSharkPlugin.ProviderId] = "Douban_26862290",
                    [MetadataProvider.Tmdb.ToString()] = "65942",
                    [MetadataProvider.Imdb.ToString()] = "tt5705718",
                    [MetadataProvider.Tvdb.ToString()] = "305089",
                },
            };
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.FindByPath(infoPath, true)).Returns(currentSeries);
            var refreshIntentStore = new InMemoryTmdbCorrectionRefreshIntentStore();
            var itemId = currentSeries.Id.ToString("D", CultureInfo.InvariantCulture);
            var explicitRefreshContext = LlmProviderFlowTestHelpers.CreateExplicitSearchMissingHttpContext(itemId);
            Assert.IsTrue(TmdbCorrectionRefreshIntentClassifier.TryResolveExplicitSearchMissingMetadataRefreshItemId(explicitRefreshContext, out var refreshItemId));
            refreshIntentStore.Save(refreshItemId, infoPath);
            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = explicitRefreshContext,
            };
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor,
                libraryManager: libraryManager.Object,
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdService,
                tmdbCorrectionRefreshIntentStore: refreshIntentStore);
            var firstInfo = CreateSeriesInfo("Re：从零开始的休息时间", infoPath);
            firstInfo.Year = 2016;
            firstInfo.IsAutomated = false;
            firstInfo.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "26862290",
                [MetaSharkPlugin.ProviderId] = "Douban_26862290",
                [MetadataProvider.Tmdb.ToString()] = "65942",
                [MetadataProvider.Imdb.ToString()] = "tt5705718",
                [MetadataProvider.Tvdb.ToString()] = "305089",
            };

            var firstResult = await provider.GetMetadata(firstInfo, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(firstResult.HasMetadata);
            Assert.AreEqual("Re：从零开始的异世界生活", firstResult.Item!.Name);
            Assert.AreEqual("Tmdb_65942", currentSeries.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.IsFalse(currentSeries.ProviderIds.ContainsKey(BaseProvider.DoubanProviderId));
            Assert.AreEqual(1, externalIdService.CorrectionRequests.Count, "第一次 search-missing refresh 没有持久化 map 时仍应进入 LLM 纠错。");
            configuration.LlmTmdbCorrectionMap = "series:douban:26862290=tmdb:65942";

            externalIdService.EnqueueExistingProviderDecision(LlmAssistTriggerDecision.Allowed("StaleExternalIdConflict"));
            externalIdService.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.VerifiedExistingTmdb("65942", "ExistingTmdbVerified"));
            httpContextAccessor.HttpContext = LlmProviderFlowTestHelpers.CreateQuerylessRefreshHttpContext(itemId);
            var firstFollowUpInfo = CreateSeriesInfo("Re：从零开始的休息时间", infoPath);
            firstFollowUpInfo.Year = 2016;
            firstFollowUpInfo.IsAutomated = false;
            firstFollowUpInfo.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "26862290",
                [MetaSharkPlugin.ProviderId] = "Tmdb_65942",
                [MetadataProvider.Tmdb.ToString()] = "65942",
                [MetadataProvider.Imdb.ToString()] = "tt5705718",
                [MetadataProvider.Tvdb.ToString()] = "305089",
            };

            var firstFollowUpResult = await provider.GetMetadata(firstFollowUpInfo, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(firstFollowUpResult.HasMetadata);
            Assert.AreEqual("Re：从零开始的异世界生活", firstFollowUpResult.Item!.Name);

            externalIdService.EnqueueExistingProviderDecision(LlmAssistTriggerDecision.Allowed("StaleExternalIdConflict"));
            externalIdService.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.VerifiedExistingTmdb("65942", "ExistingTmdbVerified"));
            var followUpInfo = CreateSeriesInfo("Re：从零开始的休息时间", infoPath);
            followUpInfo.Year = 2016;
            followUpInfo.IsAutomated = false;
            followUpInfo.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "26862290",
                [MetaSharkPlugin.ProviderId] = "Tmdb_65942",
                [MetadataProvider.Tmdb.ToString()] = "65942",
                [MetadataProvider.Imdb.ToString()] = "tt5705718",
                [MetadataProvider.Tvdb.ToString()] = "305089",
            };

            var followUpResult = await provider.GetMetadata(followUpInfo, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdService.CorrectionRequests.Count, "持久化 Douban -> TMDb 纠错映射命中后，后续 queryless provider 调用应直接走 TMDb，不再重复调用 LLM 纠错。");
            Assert.IsTrue(followUpResult.HasMetadata);
            Assert.AreEqual("Re：从零开始的异世界生活", followUpResult.Item!.Name);
            Assert.AreEqual("主线简介", followUpResult.Item.Overview);
            Assert.AreEqual("65942", followUpResult.Item.GetProviderId(MetadataProvider.Tmdb));
            Assert.IsNull(followUpResult.Item.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Tmdb_65942", followUpResult.Item.GetProviderId(MetaSharkPlugin.ProviderId));
        }

        [TestMethod]
        public async Task GetMetadata_TmdbMetaSourceWithoutDoubanCorrectionMapWithDefaultScraperMode_UsesDefaultDoubanBranch()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration());
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var staleDoubanSubject = CreateDoubanSubject("26862290", "Re：从零开始的休息时间", 2016);
            SeedDoubanSubject(doubanApi, staleDoubanSubject);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 65942, "zh-CN", CreateTmdbSeries(65942, "Re：从零开始的异世界生活", "主线简介", "tt5705718", "305089"));
            var infoPath = "/mnt/media/TV/Re：从零开始的异世界生活 (2016)";
            var currentSeries = new TrackingSeries
            {
                Id = Guid.NewGuid(),
                Name = "Re：从零开始的异世界生活",
                Overview = "主线简介",
                Path = infoPath,
                ProviderIds = new Dictionary<string, string>
                {
                    [BaseProvider.DoubanProviderId] = "26862290",
                    [MetaSharkPlugin.ProviderId] = "Tmdb_65942",
                    [MetadataProvider.Tmdb.ToString()] = "65942",
                    [MetadataProvider.Imdb.ToString()] = "tt5705718",
                    [MetadataProvider.Tvdb.ToString()] = "305089",
                },
            };
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateExplicitRefreshContextAccessor(currentSeries.Id.ToString("D", CultureInfo.InvariantCulture)),
                libraryManager: CreateLibraryManager(currentSeries).Object,
                doubanApi: doubanApi,
                tmdbApi: tmdbApi);
            var info = CreateSeriesInfo("Re：从零开始的异世界生活", infoPath);
            info.Year = 2016;
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "26862290",
                [MetaSharkPlugin.ProviderId] = "Tmdb_65942",
                [MetadataProvider.Tmdb.ToString()] = "65942",
                [MetadataProvider.Imdb.ToString()] = "tt5705718",
                [MetadataProvider.Tvdb.ToString()] = "305089",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("Re：从零开始的休息时间", result.Item!.Name);
            Assert.AreEqual("Re：从零开始的休息时间简介", result.Item.Overview);
            Assert.AreEqual("65942", result.Item.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("Douban_26862290", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual("26862290", result.Item.GetProviderId(BaseProvider.DoubanProviderId));
        }

        [TestMethod]
        public async Task GetMetadata_LlmDoubanCorrectionMapWithDefaultScraperMode_UsesTmdbMetadataSkipsDoubanBranchAndDoesNotRequeryCorrection()
        {
            EnsurePluginInstance();
            var configuration = CreateLlmConfiguration(enableTmdbCorrection: true);
            configuration.LlmTmdbCorrectionMap = "series:douban:26862290=tmdb:65942";
            ReplacePluginConfiguration(configuration);
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var staleDoubanSubject = CreateDoubanSubject("26862290", "Re：从零开始的休息时间", 2016);
            SeedDoubanSubject(doubanApi, staleDoubanSubject);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 65942, "zh-CN", CreateTmdbSeries(65942, "Re：从零开始的异世界生活", "主线简介", "tt5705718", "305089"));
            var infoPath = "/mnt/media/TV/Re：从零开始的异世界生活 (2016)";
            var currentSeries = new TrackingSeries
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
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateExplicitRefreshContextAccessor(currentSeries.Id.ToString("D", CultureInfo.InvariantCulture)),
                libraryManager: CreateLibraryManager(currentSeries).Object,
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdService);
            var info = CreateSeriesInfo("Re：从零开始的休息时间", infoPath);
            info.Year = 2016;
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "26862290",
                [MetaSharkPlugin.ProviderId] = "Douban_26862290",
                [MetadataProvider.Tmdb.ToString()] = "65942",
            };
            var stages = new List<string>();
            SeriesProvider.TestTraceSink = trace => stages.Add(trace.Stage);

            try
            {
                var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

                Assert.IsFalse(stages.Contains("BeforeDoubanMetadata"), "Douban 纠错到 TMDb 的条目应跳过默认 Douban 元数据分支。");
                Assert.IsTrue(stages.Contains("BeforeTmdbMetadata"), "Douban 纠错到 TMDb 的条目应直接进入 TMDb 元数据分支。");
                Assert.AreEqual(0, externalIdService.ExistingProviderDecisionRequests.Count, "配置里已有 Douban -> TMDb 纠错映射时，不应再次评估 LLM 纠错入口。");
                Assert.AreEqual(0, externalIdService.CorrectionRequests.Count, "配置里已有 Douban -> TMDb 纠错映射时，不应再次调用 LLM 纠错。");
                Assert.IsTrue(result.HasMetadata);
                Assert.AreEqual("Re：从零开始的异世界生活", result.Item!.Name);
                Assert.AreEqual("主线简介", result.Item.Overview);
                Assert.AreEqual("65942", result.Item.GetProviderId(MetadataProvider.Tmdb));
                Assert.AreEqual("Tmdb_65942", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
                Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId));
            }
            finally
            {
                SeriesProvider.TestTraceSink = null;
            }
        }

        [TestMethod]
        public async Task GetMetadata_LlmDoubanCorrectionMapWithDefaultScraperModeDisabled_StillUsesDefaultDoubanBranch()
        {
            EnsurePluginInstance();
            var configuration = CreateLlmConfiguration();
            configuration.EnableLlmTmdbCorrectionPersistence = false;
            configuration.LlmTmdbCorrectionMap = "series:douban:26862290=tmdb:65942";
            ReplacePluginConfiguration(configuration);
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var staleDoubanSubject = CreateDoubanSubject("26862290", "Re：从零开始的休息时间", 2016);
            SeedDoubanSubject(doubanApi, staleDoubanSubject);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 65942, "zh-CN", CreateTmdbSeries(65942, "Re：从零开始的异世界生活", "主线简介", "tt5705718", "305089"));
            var infoPath = "/mnt/media/TV/Re：从零开始的异世界生活 (2016)";
            var currentSeries = new TrackingSeries
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
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateExplicitRefreshContextAccessor(currentSeries.Id.ToString("D", CultureInfo.InvariantCulture)),
                libraryManager: CreateLibraryManager(currentSeries).Object,
                doubanApi: doubanApi,
                tmdbApi: tmdbApi);
            var info = CreateSeriesInfo("Re：从零开始的休息时间", infoPath);
            info.Year = 2016;
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "26862290",
                [MetaSharkPlugin.ProviderId] = "Douban_26862290",
                [MetadataProvider.Tmdb.ToString()] = "65942",
            };
            var stages = new List<string>();
            SeriesProvider.TestTraceSink = trace => stages.Add(trace.Stage);

            try
            {
                var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

                Assert.IsTrue(stages.Contains("BeforeDoubanMetadata"), "关闭持久化开关后，已有 map 不应抢占默认 Douban 分支。");
                Assert.IsFalse(stages.Contains("BeforeTmdbMetadata"), "关闭持久化开关后，默认模式不应因为旧 map 直接进入 TMDb 分支。");
                Assert.IsTrue(result.HasMetadata);
                Assert.AreEqual("Re：从零开始的休息时间", result.Item!.Name);
                Assert.AreEqual("Re：从零开始的休息时间简介", result.Item.Overview);
                Assert.AreEqual("65942", result.Item.GetProviderId(MetadataProvider.Tmdb));
                Assert.AreEqual("Douban_26862290", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
                Assert.AreEqual("26862290", result.Item.GetProviderId(BaseProvider.DoubanProviderId));
            }
            finally
            {
                SeriesProvider.TestTraceSink = null;
            }
        }

        [TestMethod]
        public async Task GetMetadata_SemanticConflict_WithMultipleExistingProviderIds_UsesTmdbMetadataAndClearsStaleDouban()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableTmdbCorrection: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var doubanSubject = CreateDoubanSubject("semantic-conflict-series-douban", "主线剧集", 2024);
            SeedDoubanSubject(doubanApi, doubanSubject);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 222, "zh-CN", CreateTmdbSeries(222, "主线剧集", "修正后简介", "ttmain222", "tvdb-main-222"));
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueExistingProviderDecision(LlmAssistTriggerDecision.Allowed("StaleExternalIdConflict"));
            externalIdService.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.Verified("222", "test semantic conflict replacement"));
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateExplicitRefreshContextAccessor(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdService);
            var info = CreateSeriesInfo("主线剧集", "/mnt/media/Shows/主线剧集 (2024)/短篇特典/第01集.mkv");
            info.IsAutomated = false;
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "semantic-conflict-series-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_semantic-conflict-series-douban",
                [MetadataProvider.Tmdb.ToString()] = "111",
                [MetadataProvider.Imdb.ToString()] = "ttspinoff111",
                [MetadataProvider.Tvdb.ToString()] = "tvdb-spinoff-111",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdService.ExistingProviderDecisionRequests.Count);
            Assert.AreEqual(1, externalIdService.CorrectionRequests.Count, "已有 TMDb/TVDB/IMDb 时，语义冲突样例仍应进入 correction evaluation。");
            Assert.AreEqual(0, externalIdService.Requests.Count, "已有 TMDb 的 correction 路径不应退回普通 external-id resolver。");
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("主线剧集", result.Item!.Name);
            Assert.AreEqual("修正后简介", result.Item.Overview);
            Assert.AreEqual("222", info.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("222", result.Item!.GetProviderId(MetadataProvider.Tmdb));
            Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Tmdb_222", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual("ttspinoff111", result.Item.GetProviderId(MetadataProvider.Imdb));
            Assert.AreEqual("tvdb-spinoff-111", result.Item.GetProviderId(MetadataProvider.Tvdb));
            Assert.AreEqual("111", externalIdService.CorrectionRequests[0].OldTmdbId);
            Assert.AreEqual(DefaultScraperSemantic.UserRefresh, externalIdService.CorrectionRequests[0].Semantic);
        }

        [TestMethod]
        public async Task GetMetadata_TmdbCorrection_BridgedSearchMissingRefreshAndStaleConflict_UsesTmdbMetadataAndClearsStaleDouban()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableTmdbCorrection: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var doubanSubject = CreateDoubanSubject("whitelisted-stale-series-douban", "主线剧集", 2024);
            SeedDoubanSubject(doubanApi, doubanSubject);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 222, "zh-CN", CreateTmdbSeries(222, "主线剧集", "修正后简介", "ttmain222", "tvdb-main-222"));
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueExistingProviderDecision(LlmAssistTriggerDecision.Allowed("StaleExternalIdConflict"));
            externalIdService.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.Verified("222", "test whitelisted stale replacement"));
            var refreshIntentStore = new InMemoryTmdbCorrectionRefreshIntentStore();
            var itemId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
            var refreshContext = LlmProviderFlowTestHelpers.CreateExplicitSearchMissingHttpContext(itemId);
            Assert.IsTrue(TmdbCorrectionRefreshIntentClassifier.TryResolveExplicitSearchMissingMetadataRefreshItemId(refreshContext, out var refreshItemId));
            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = refreshContext,
            };
            refreshIntentStore.Save(refreshItemId, "/mnt/media/Shows/主线剧集 (2024)/短篇特典/第01集.mkv");
            httpContextAccessor.HttpContext!.Request.QueryString = QueryString.Empty;
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor,
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdService,
                tmdbCorrectionRefreshIntentStore: refreshIntentStore);
            var info = CreateSeriesInfo("休息时间短篇", "/mnt/media/Shows/主线剧集 (2024)/短篇特典/第01集.mkv");
            info.IsAutomated = false;
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "whitelisted-stale-series-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_whitelisted-stale-series-douban",
                [MetadataProvider.Tmdb.ToString()] = "111",
                [MetadataProvider.Imdb.ToString()] = "ttspinoff111",
                [MetadataProvider.Tvdb.ToString()] = "tvdb-spinoff-111",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdService.ExistingProviderDecisionRequests.Count);
            Assert.AreEqual(DefaultScraperSemantic.UserRefresh, externalIdService.ExistingProviderDecisionRequests[0].Semantic);
            Assert.AreEqual(1, externalIdService.CorrectionRequests.Count, "bridge search-missing refresh + 明显冲突时应进入 correction evaluation。");
            Assert.AreEqual(DefaultScraperSemantic.UserRefresh, externalIdService.CorrectionRequests[0].Semantic);
            Assert.AreEqual(QueryString.Empty.Value, externalIdService.CorrectionRequests[0].HttpContext!.Request.QueryString.Value);
            Assert.AreEqual(0, externalIdService.Requests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("主线剧集", result.Item!.Name);
            Assert.AreEqual("修正后简介", result.Item.Overview);
            Assert.AreEqual("222", info.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("222", result.Item!.GetProviderId(MetadataProvider.Tmdb));
            Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Tmdb_222", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual("ttspinoff111", result.Item.GetProviderId(MetadataProvider.Imdb));
            Assert.AreEqual("tvdb-spinoff-111", result.Item.GetProviderId(MetadataProvider.Tvdb));
            Assert.IsTrue(refreshIntentStore.HasPending(refreshItemId, info.Path), "桥接 search-missing intent 需要保留到短 TTL 窗口，供 Jellyfin 同轮后续 queryless provider 调用复用。 ");
        }

        [TestMethod]
        public async Task GetMetadata_ManualIdentifyCorrectionVerified_UsesTmdbMetadataAndClearsStaleDouban()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableTmdbCorrection: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var doubanApi = new DoubanApi(loggerFactory);
            var doubanSubject = CreateDoubanSubject("manual-identify-series-douban", "手动识别纠错剧集", 2024);
            SeedDoubanSubject(doubanApi, doubanSubject);
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 222, "zh-CN", CreateTmdbSeries(222, "手动识别纠错后剧集", "纠错后简介", "tt222", "tvdb222"));
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.Verified("222", "test verified replacement"));
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateManualMatchContextAccessor(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdService);
            var info = CreateSeriesInfo("手动识别纠错剧集", "/mnt/media/TV/手动识别纠错剧集");
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "manual-identify-series-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_manual-identify-series-douban",
                [MetadataProvider.Tmdb.ToString()] = "111",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdService.CorrectionRequests.Count);
            Assert.AreEqual(DefaultScraperSemantic.ManualMatch, externalIdService.CorrectionRequests[0].Semantic);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("手动识别纠错后剧集", result.Item!.Name);
            Assert.AreEqual("纠错后简介", result.Item.Overview);
            Assert.AreEqual("222", result.Item!.GetProviderId(MetadataProvider.Tmdb));
            Assert.IsNull(result.Item.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Tmdb_222", result.Item.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual("tt222", result.Item.GetProviderId(MetadataProvider.Imdb));
            Assert.AreEqual("tvdb222", result.Item.GetProviderId(MetadataProvider.Tvdb));
        }

        [TestMethod]
        public async Task GetMetadata_OverwriteRefreshCorrectionVerified_DoesNotCallOrdinaryLlmPaths()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableEpisodeGroupMappingAssist: true, enableTmdbCorrection: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            var correctedSeries = CreateTmdbSeries(222, "覆盖纠错剧集", "覆盖纠错简介", null, null);
            correctedSeries.EpisodeGroups = new ResultContainer<TvGroupCollection>
            {
                Results = new List<TvGroupCollection>
                {
                    new TvGroupCollection
                    {
                        Id = "overwrite-correction-group",
                        Name = "覆盖纠错映射组",
                        Type = TvGroupType.Absolute,
                        GroupCount = 1,
                        EpisodeCount = 1,
                    },
                },
            };
            SeedTmdbSeries(tmdbApi, 222, "zh-CN", correctedSeries);
            ExplicitEpisodeGroupMappingTestHelper.SeedEpisodeGroupById(tmdbApi, "overwrite-correction-group", "zh-CN");
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.Verified("222", "test verified replacement"));
            externalIdService.EnqueueResult(CreateExternalIdResolutionResult("TMDb", "9999", "Series", MetadataProvider.Tmdb.ToString()));
            var textLlm = CreateLlmService("不应调用覆盖文本", 2024);
            var llmApi = new RecordingLlmApi("overwrite-correction-group", 0.95);
            var refreshCalls = new List<QueueRefreshCall>();
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateExplicitRefreshContextAccessor(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture), replaceAllMetadata: true),
                tmdbApi: tmdbApi,
                llmService: textLlm,
                llmEpisodeGroupMappingProviderAssistService: CreateEpisodeGroupMappingProviderAssistService(loggerFactory, llmApi, tmdbApi, Array.Empty<BaseItem>(), refreshCalls),
                llmExternalIdResolutionService: externalIdService);
            var info = CreateSeriesInfo("覆盖纠错剧集", "/mnt/media/TV/覆盖纠错剧集");
            info.ProviderIds = new Dictionary<string, string>
            {
                [MetaSharkPlugin.ProviderId] = "Tmdb_111",
                [MetadataProvider.Tmdb.ToString()] = "111",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, externalIdService.CorrectionRequests.Count);
            Assert.AreEqual(0, externalIdService.Requests.Count);
            Assert.AreEqual(0, textLlm.Requests.Count);
            Assert.AreEqual(0, llmApi.Prompts.Count);
            Assert.AreEqual(0, refreshCalls.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("222", result.Item!.GetProviderId(MetadataProvider.Tmdb));
        }

        [TestMethod]
        public async Task GetMetadata_AutomaticRefreshWithExistingTmdb_DoesNotInvokeCorrection()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableTmdbCorrection: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 111, "zh-CN", CreateTmdbSeries(111, "自动拒绝纠错剧集", "自动简介", null, null));
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.Verified("222", "test verified replacement"));
            var provider = CreateProvider(
                loggerFactory,
                LlmProviderFlowTestHelpers.CreateAutomaticRefreshContextAccessor(),
                tmdbApi: tmdbApi,
                llmExternalIdResolutionService: externalIdService);
            var info = CreateSeriesInfo("自动拒绝纠错剧集", "/mnt/media/TV/自动拒绝纠错剧集");
            info.IsAutomated = true;
            info.ProviderIds = new Dictionary<string, string>
            {
                [MetaSharkPlugin.ProviderId] = "Tmdb_111",
                [MetadataProvider.Tmdb.ToString()] = "111",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, externalIdService.CorrectionRequests.Count);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("111", result.Item!.GetProviderId(MetadataProvider.Tmdb));
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
        public async Task GetMetadata_BridgedSearchMissingVerifiedTmdb_InvokesExternalIdServiceAndEpisodeGroupMapping()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableEpisodeGroupMappingAssist: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var traces = new List<SeriesProvider.SeriesFlowTrace>();
            SeriesProvider.TestTraceSink = traces.Add;
            var tmdbApi = new TmdbApi(loggerFactory);
            var tvShow = CreateTmdbSeries(8864, "桥接映射剧集", "桥接映射简介", null, null);
            tvShow.EpisodeGroups = new ResultContainer<TvGroupCollection>
            {
                Results = new List<TvGroupCollection>
                {
                    new TvGroupCollection
                    {
                        Id = "bridged-group",
                        Name = "桥接映射组",
                        Type = TvGroupType.Absolute,
                        GroupCount = 1,
                        EpisodeCount = 1,
                    },
                },
            };
            SeedTmdbSeries(tmdbApi, 8864, "zh-CN", tvShow);
            ExplicitEpisodeGroupMappingTestHelper.SeedEpisodeGroupById(tmdbApi, "bridged-group", "zh-CN");
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueResult(CreateExternalIdResolutionResult("TMDb", "8864", "Series", MetadataProvider.Tmdb.ToString()));
            var llmApi = new RecordingLlmApi("bridged-group", 0.95);
            var mappedSeries = CreateSeries(Guid.NewGuid(), "桥接映射剧集", "8864");
            mappedSeries.Path = "/mnt/media/TV/桥接映射剧集";
            var refreshCalls = new List<QueueRefreshCall>();
            var refreshIntentStore = new InMemoryTmdbCorrectionRefreshIntentStore();
            var itemId = mappedSeries.Id.ToString("N", CultureInfo.InvariantCulture);
            var refreshContext = LlmProviderFlowTestHelpers.CreateExplicitSearchMissingHttpContext(itemId);
            Assert.IsTrue(TmdbCorrectionRefreshIntentClassifier.TryResolveExplicitSearchMissingMetadataRefreshItemId(refreshContext, out var refreshItemId));
            refreshIntentStore.Save(refreshItemId, "/mnt/media/TV/桥接映射剧集");
            Assert.IsTrue(refreshIntentStore.HasPending(refreshItemId, "/mnt/media/TV/桥接映射剧集"), "test setup should seed bridge intent");
            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = LlmProviderFlowTestHelpers.CreateQuerylessRefreshHttpContext(itemId),
            };
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor,
                libraryManager: CreateLibraryManager(mappedSeries).Object,
                tmdbApi: tmdbApi,
                llmEpisodeGroupMappingProviderAssistService: CreateEpisodeGroupMappingProviderAssistService(loggerFactory, llmApi, tmdbApi, new[] { mappedSeries }, refreshCalls),
                llmExternalIdResolutionService: externalIdService,
                tmdbCorrectionRefreshIntentStore: refreshIntentStore);
            var info = CreateSeriesInfo("桥接映射剧集", "/mnt/media/TV/桥接映射剧集");
            info.IsAutomated = false;

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(traces.Count > 0, "expected provider flow traces");
            Assert.AreEqual(true, traces[0].HasBridgedExplicitSearchMissingMetadataRefreshIntent, "provider should resolve bridge intent before external-id gate");
            Assert.AreEqual(1, externalIdService.ExistingProviderDecisionRequests.Count, "桥接映射剧集应进入 existing-provider decision。 ");
            Assert.AreEqual(1, externalIdService.Requests.Count, "桥接映射剧集应进入 external-id resolve。 ");
            Assert.AreEqual(true, externalIdService.LastExistingProviderDecisionBridgedFlag);
            Assert.AreEqual(true, externalIdService.LastResolveBridgedFlag);
            CollectionAssert.AreEqual(
                new[] { "Initial", "BeforeExternalIdResolve", "BeforeMetadataAssist", "BeforeTmdbMetadata" },
                traces.Select(x => x.Stage).ToArray(),
                "桥接 search-missing 在 provider 阶段变成 queryless refresh 后，仍应进入 external-id resolver 并用验证后的 TMDb 元数据。 actual=" + string.Join(",", traces.Select(x => x.Stage)));
            Assert.AreEqual(false, traces.Last().HasDoubanMeta);
            Assert.AreEqual(false, traces.Last().HasTmdbMeta);
            Assert.AreEqual(true, traces.Last().HasBridgedExplicitSearchMissingMetadataRefreshIntent);
            Assert.IsTrue(refreshIntentStore.HasPending(refreshItemId, info.Path), "桥接映射剧集在 metadata flow 内应保留 refresh intent，供 Jellyfin 同轮后续 provider 调用复用。 ");
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("8864", result.Item!.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual(1, llmApi.Prompts.Count, "验证后的 TMDbId 应继续触发剧集组映射 assist。 ");
            AssertQueuedSeries(refreshCalls, mappedSeries.Id);
        }

        [TestMethod]
        public async Task TryAssistSeriesMetadataWithLlm_WhenProviderTimeRefreshIsQuerylessBridgeStillRejectsImplicitRefresh()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableEpisodeGroupMappingAssist: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var llmService = CreateLlmService("桥接前置剧集", 2024);
            var refreshIntentStore = new InMemoryTmdbCorrectionRefreshIntentStore();
            var itemId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            var requestTimeContext = LlmProviderFlowTestHelpers.CreateExplicitSearchMissingHttpContext(itemId);
            Assert.IsTrue(TmdbCorrectionRefreshIntentClassifier.TryResolveExplicitSearchMissingMetadataRefreshItemId(requestTimeContext, out var refreshItemId));
            refreshIntentStore.Save(refreshItemId, "/mnt/media/TV/桥接前置剧集");
            var providerTimeContextAccessor = new HttpContextAccessor
            {
                HttpContext = LlmProviderFlowTestHelpers.CreateQuerylessRefreshHttpContext(itemId),
            };
            var provider = CreateProvider(
                loggerFactory,
                providerTimeContextAccessor,
                llmService: llmService,
                tmdbCorrectionRefreshIntentStore: refreshIntentStore);
            var info = CreateSeriesInfo("桥接前置剧集", "/mnt/media/TV/桥接前置剧集");
            var method = typeof(SeriesProvider).GetMethod("TryAssistSeriesMetadataWithLlmAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(method);

            var task = (Task<LlmScrapingAssistResult>)method!.Invoke(provider, new object[] { info, DefaultScraperSemantic.UserRefresh, CancellationToken.None })!;
            var result = await task.ConfigureAwait(false);

            Assert.AreEqual(LlmScrapingAssistStatus.NotTriggered, result.Status);
            Assert.AreEqual("ImplicitRefreshRejected", result.Diagnostic);
            Assert.AreEqual(0, llmService.Requests.Count, "当前 helper 若仍只看 plain trigger policy，就会在最早 assist gate 被挡住。 ");
        }

        [TestMethod]
        public async Task GetMetadata_BridgedSearchMissingRefresh_CurrentSetupStaysOnDoubanMetadataBranch()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(CreateLlmConfiguration(enableEpisodeGroupMappingAssist: true, enableTmdbCorrection: true));
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var traces = new List<SeriesProvider.SeriesFlowTrace>();
            SeriesProvider.TestTraceSink = traces.Add;
            var doubanApi = new DoubanApi(loggerFactory);
            var doubanSubject = CreateDoubanSubject("bridge-share-series-douban", "桥接共享剧集", 2024);
            SeedDoubanSubject(doubanApi, doubanSubject);
            var tmdbApi = new TmdbApi(loggerFactory);
            var tvShow = CreateTmdbSeries(8865, "桥接共享剧集", "桥接共享简介", "tt8865", "tvdb8865");
            tvShow.EpisodeGroups = new ResultContainer<TvGroupCollection>
            {
                Results = new List<TvGroupCollection>
                {
                    new TvGroupCollection
                    {
                        Id = "shared-group",
                        Name = "共享映射组",
                        Type = TvGroupType.Absolute,
                        GroupCount = 1,
                        EpisodeCount = 1,
                    },
                },
            };
            SeedTmdbSeries(tmdbApi, 8865, "zh-CN", tvShow);
            ExplicitEpisodeGroupMappingTestHelper.SeedEpisodeGroupById(tmdbApi, "shared-group", "zh-CN");
            var externalIdService = new RecordingLlmExternalIdResolutionService();
            externalIdService.EnqueueExistingProviderDecision(LlmAssistTriggerDecision.Allowed("StaleExternalIdConflict"));
            externalIdService.EnqueueCorrectionResult(LlmTmdbIdCorrectionResult.Verified("8865", "test shared bridge replacement"));
            var llmApi = new RecordingLlmApi("shared-group", 0.95);
            var mappedSeries = CreateSeries(Guid.NewGuid(), "桥接共享剧集", "8865");
            mappedSeries.Path = "/mnt/media/TV/桥接共享剧集";
            var refreshCalls = new List<QueueRefreshCall>();
            var refreshIntentStore = new InMemoryTmdbCorrectionRefreshIntentStore();
            var itemId = mappedSeries.Id.ToString("N", CultureInfo.InvariantCulture);
            var refreshContext = LlmProviderFlowTestHelpers.CreateExplicitSearchMissingHttpContext(itemId);
            Assert.IsTrue(TmdbCorrectionRefreshIntentClassifier.TryResolveExplicitSearchMissingMetadataRefreshItemId(refreshContext, out var refreshItemId));
            refreshIntentStore.Save(refreshItemId, "/mnt/media/TV/桥接共享剧集");
            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = LlmProviderFlowTestHelpers.CreateQuerylessRefreshHttpContext(itemId),
            };
            var provider = CreateProvider(
                loggerFactory,
                httpContextAccessor,
                libraryManager: CreateLibraryManager(new[] { mappedSeries }).Object,
                doubanApi: doubanApi,
                tmdbApi: tmdbApi,
                llmEpisodeGroupMappingProviderAssistService: CreateEpisodeGroupMappingProviderAssistService(loggerFactory, llmApi, tmdbApi, new[] { mappedSeries }, refreshCalls),
                llmExternalIdResolutionService: externalIdService,
                tmdbCorrectionRefreshIntentStore: refreshIntentStore);
            var info = CreateSeriesInfo("桥接共享剧集", "/mnt/media/TV/桥接共享剧集");
            info.ProviderIds = new Dictionary<string, string>
            {
                [BaseProvider.DoubanProviderId] = "bridge-share-series-douban",
                [MetaSharkPlugin.ProviderId] = "Douban_bridge-share-series-douban",
                [MetadataProvider.Tmdb.ToString()] = "111",
            };

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            CollectionAssert.AreEqual(
                new[] { "Initial", "BeforeDoubanMetadata" },
                traces.Select(x => x.Stage).ToArray(),
                "当前共享桥接 setup 在现有语义下应直接走 Douban metadata 主链。 ");
            Assert.AreEqual(true, traces.Last().HasDoubanMeta);
            Assert.AreEqual(false, traces.Last().HasTmdbMeta);
            Assert.AreEqual(true, traces.Last().HasBridgedExplicitSearchMissingMetadataRefreshIntent);
            Assert.IsTrue(refreshIntentStore.HasPending(refreshItemId, info.Path), "共享桥接剧集在 metadata flow 内应保留 refresh intent，供 Jellyfin 同轮后续 provider 调用复用。 ");
            Assert.AreEqual(0, externalIdService.ExistingProviderDecisionRequests.Count, "当前共享桥接 setup 不会进入 existing-provider decision。 ");
            Assert.AreEqual(0, externalIdService.CorrectionRequests.Count, "当前共享桥接 setup 不会进入 correction request。 ");
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("111", result.Item!.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("bridge-share-series-douban", result.Item.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.AreEqual(0, llmApi.Prompts.Count, "当前共享桥接 setup 不会进入映射 assist。 ");
            Assert.AreEqual(0, refreshCalls.Count, "当前共享桥接 setup 不会排队映射刷新。 ");
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
            ILlmExternalIdResolutionService? llmExternalIdResolutionService = null,
            ITmdbCorrectionRefreshIntentStore? tmdbCorrectionRefreshIntentStore = null,
            ILlmTmdbCorrectionMapPersistenceService? llmTmdbCorrectionMapPersistenceService = null)
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
                llmExternalIdResolutionService,
                tmdbCorrectionRefreshIntentStore,
                llmTmdbCorrectionMapPersistenceService);
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

        private static PluginConfiguration CreateLlmConfiguration(bool enableLlm = true, bool allowTextCompletion = true, bool enableEpisodeGroupMappingAssist = false, string defaultScraperMode = PluginConfiguration.DefaultScraperModeDefault, bool enableTmdbMatch = true, bool enableTmdbCorrection = false)
        {
            return new PluginConfiguration
            {
                EnableLlmAssist = enableLlm,
                EnableLlmTmdbIdCorrection = enableTmdbCorrection,
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
            List<QueueRefreshCall> queueCalls,
            ITmdbEpisodeGroupMapPersistenceService? persistenceService = null)
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
                new LlmEpisodeGroupMappingAssistService(llmApi, tmdbApi, EpisodeGroupMapParser.Shared, persistenceService ?? new RecordingPersistenceService()),
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

        private sealed class RecordingPersistenceService : ITmdbEpisodeGroupMapPersistenceService
        {
            private readonly TmdbEpisodeGroupMapPersistenceResult? result;

            public RecordingPersistenceService()
            {
            }

            public RecordingPersistenceService(TmdbEpisodeGroupMapPersistenceResult result)
            {
                this.result = result;
            }

            public List<PersistenceCall> Calls { get; } = new List<PersistenceCall>();

            public Task<TmdbEpisodeGroupMapPersistenceResult> TrySaveAsync(string? expectedOldMapping, string? newMapping, CancellationToken cancellationToken)
            {
                this.Calls.Add(new PersistenceCall(expectedOldMapping ?? string.Empty, newMapping ?? string.Empty));
                var resolvedResult = this.result ?? TmdbEpisodeGroupMapPersistenceResult.SavedResult(expectedOldMapping ?? string.Empty, newMapping ?? string.Empty);
                var currentConfiguration = MetaSharkPlugin.Instance?.Configuration;
                if (currentConfiguration != null)
                {
                    currentConfiguration.TmdbEpisodeGroupMap = resolvedResult.CurrentMapping;
                }

                return Task.FromResult(resolvedResult);
            }
        }

        private sealed class SequencePersistenceService : ITmdbEpisodeGroupMapPersistenceService
        {
            private readonly Queue<TmdbEpisodeGroupMapPersistenceResult> results;

            public SequencePersistenceService(params TmdbEpisodeGroupMapPersistenceResult[] results)
            {
                this.results = new Queue<TmdbEpisodeGroupMapPersistenceResult>(results ?? Array.Empty<TmdbEpisodeGroupMapPersistenceResult>());
            }

            public List<PersistenceCall> Calls { get; } = new List<PersistenceCall>();

            public Task<TmdbEpisodeGroupMapPersistenceResult> TrySaveAsync(string? expectedOldMapping, string? newMapping, CancellationToken cancellationToken)
            {
                this.Calls.Add(new PersistenceCall(expectedOldMapping ?? string.Empty, newMapping ?? string.Empty));
                var resolvedResult = this.results.Count > 0
                    ? this.results.Dequeue()
                    : TmdbEpisodeGroupMapPersistenceResult.SavedResult(expectedOldMapping ?? string.Empty, newMapping ?? string.Empty);
                var currentConfiguration = MetaSharkPlugin.Instance?.Configuration;
                if (currentConfiguration != null)
                {
                    currentConfiguration.TmdbEpisodeGroupMap = resolvedResult.CurrentMapping;
                }

                return Task.FromResult(resolvedResult);
            }
        }

        private sealed record PersistenceCall(string ExpectedOldMapping, string NewMapping);

        private sealed class RecordingLlmTmdbCorrectionMapPersistenceService : ILlmTmdbCorrectionMapPersistenceService
        {
            public List<PersistenceCall> CorrectionCalls { get; } = new List<PersistenceCall>();

            public List<PersistenceCall> CompletionCalls { get; } = new List<PersistenceCall>();

            public Task<LlmTmdbCorrectionMapPersistenceResult> TryUpsertDoubanCorrectionAsync(string mediaType, string doubanId, string tmdbId, CancellationToken cancellationToken)
            {
                var currentMapping = MetaSharkPlugin.Instance?.Configuration.LlmTmdbCorrectionMap ?? string.Empty;
                var newMapping = LlmTmdbCorrectionMapParser.Shared.UpsertDoubanCorrection(currentMapping, mediaType, doubanId, tmdbId);
                this.CorrectionCalls.Add(new PersistenceCall(currentMapping, newMapping));
                var currentConfiguration = MetaSharkPlugin.Instance?.Configuration;
                if (currentConfiguration != null)
                {
                    currentConfiguration.LlmTmdbCorrectionMap = newMapping;
                }

                return Task.FromResult(LlmTmdbCorrectionMapPersistenceResult.SavedResult(currentMapping, newMapping));
            }

            public Task<LlmTmdbCorrectionMapPersistenceResult> TryUpsertDoubanCompletionAsync(string mediaType, string doubanId, string tmdbId, CancellationToken cancellationToken)
            {
                var currentMapping = MetaSharkPlugin.Instance?.Configuration.LlmTmdbCompletionMap ?? string.Empty;
                var newMapping = LlmTmdbCorrectionMapParser.Shared.UpsertDoubanCorrection(currentMapping, mediaType, doubanId, tmdbId);
                this.CompletionCalls.Add(new PersistenceCall(currentMapping, newMapping));
                var currentConfiguration = MetaSharkPlugin.Instance?.Configuration;
                if (currentConfiguration != null)
                {
                    currentConfiguration.LlmTmdbCompletionMap = newMapping;
                }

                return Task.FromResult(LlmTmdbCorrectionMapPersistenceResult.SavedResult(currentMapping, newMapping));
            }
        }

        private sealed class RecordingLlmExternalIdResolutionService : ILlmExternalIdResolutionService
        {
            private readonly Queue<LlmAssistTriggerDecision> queuedExistingProviderDecisions = new Queue<LlmAssistTriggerDecision>();
            private readonly Queue<LlmExternalIdResolutionResult> queuedResults = new Queue<LlmExternalIdResolutionResult>();
            private readonly Queue<LlmTmdbIdCorrectionResult> queuedCorrectionResults = new Queue<LlmTmdbIdCorrectionResult>();

            public List<LlmExternalIdResolutionRequest> ExistingProviderDecisionRequests { get; } = new List<LlmExternalIdResolutionRequest>();

            public List<LlmExternalIdResolutionRequest> Requests { get; } = new List<LlmExternalIdResolutionRequest>();

            public List<LlmTmdbIdCorrectionRequest> CorrectionRequests { get; } = new List<LlmTmdbIdCorrectionRequest>();

            public bool? LastExistingProviderDecisionBridgedFlag => this.ExistingProviderDecisionRequests.LastOrDefault()?.HasBridgedExplicitSearchMissingMetadataRefreshIntent;

            public bool? LastResolveBridgedFlag => this.Requests.LastOrDefault()?.HasBridgedExplicitSearchMissingMetadataRefreshIntent;

            public bool? LastCorrectionBridgedFlag => this.CorrectionRequests.LastOrDefault()?.HasBridgedExplicitSearchMissingMetadataRefreshIntent;

            public void EnqueueExistingProviderDecision(LlmAssistTriggerDecision decision)
            {
                this.queuedExistingProviderDecisions.Enqueue(decision);
            }

            public Task<LlmAssistTriggerDecision> EvaluateExistingProviderIdsAsync(LlmExternalIdResolutionRequest request, CancellationToken cancellationToken)
            {
                this.ExistingProviderDecisionRequests.Add(request);
                var decision = this.queuedExistingProviderDecisions.Count > 0
                    ? this.queuedExistingProviderDecisions.Dequeue()
                    : LlmAssistTriggerDecision.Allowed("No queued existing-provider decision.");
                return Task.FromResult(decision);
            }

            public void EnqueueResult(LlmExternalIdResolutionResult result)
            {
                this.queuedResults.Enqueue(result);
            }

            public void EnqueueCorrectionResult(LlmTmdbIdCorrectionResult result)
            {
                this.queuedCorrectionResults.Enqueue(result);
            }

            public Task<LlmExternalIdResolutionResult> ResolveAsync(LlmExternalIdResolutionRequest request, CancellationToken cancellationToken)
            {
                this.Requests.Add(request);
                var result = this.queuedResults.Count > 0
                    ? this.queuedResults.Dequeue()
                    : LlmExternalIdResolutionResult.NotTriggered("No queued external ID test result.");
                return Task.FromResult(result);
            }

            public Task<LlmTmdbIdCorrectionResult> TryResolveTmdbCorrectionAsync(LlmTmdbIdCorrectionRequest request, CancellationToken cancellationToken)
            {
                this.CorrectionRequests.Add(request);
                var result = this.queuedCorrectionResults.Count > 0
                    ? this.queuedCorrectionResults.Dequeue()
                    : LlmTmdbIdCorrectionResult.NoReplacement("No queued external ID correction test result.");
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

        private sealed class TrackingSeries : Series
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
