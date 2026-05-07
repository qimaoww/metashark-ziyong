using Jellyfin.Plugin.MetaShark;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.EpisodeGroupMapping;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Providers.Llm;
using Jellyfin.Plugin.MetaShark.Test.EpisodeGroupMapping;
using Jellyfin.Plugin.MetaShark.Workers;
using Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill;
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
using System.Reflection;
using TMDbLib.Objects.General;
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [DoNotParallelize]
    public class EpisodeProviderLlmAssistTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-episode-provider-llm-assist-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");

        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder => { });

        [TestCleanup]
        public void Cleanup()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableLlmAssist = false;
            MetaSharkPlugin.Instance.Configuration.EnableLlmEpisodeGroupMappingAssist = false;
            MetaSharkPlugin.Instance.Configuration.LlmAllowTextCompletion = false;
            MetaSharkPlugin.Instance.Configuration.LlmBaseUrl = string.Empty;
            MetaSharkPlugin.Instance.Configuration.LlmModel = string.Empty;
            MetaSharkPlugin.Instance.Configuration.LlmApiKey = string.Empty;
            MetaSharkPlugin.Instance.Configuration.LlmConfidenceThreshold = 0.75;
            MetaSharkPlugin.Instance.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = false;
            MetaSharkPlugin.Instance.Configuration.EnableTvdbSpecialsWithinSeasons = true;
            MetaSharkPlugin.Instance.Configuration.TmdbEpisodeGroupMap = string.Empty;
            MetaSharkPlugin.Instance.Configuration.TvdbApiKey = string.Empty;
            MetaSharkPlugin.Instance.Configuration.TvdbHost = string.Empty;
        }

        [TestMethod]
        public async Task GetMetadata_WhenLlmConfigDisabled_DoesNotCallAssist()
        {
            using var harness = CreateHarness(httpContext: LlmProviderFlowTestHelpers.CreateExplicitRefreshHttpContext(TestItemIdString()), enableLlm: false, allowTextCompletion: true);
            harness.LlmService.EnqueueResult(CreateSuccessResult(title: "启程"));

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, harness.LlmService.Requests.Count);
            Assert.AreEqual("第 1 集", result.Item!.Name);
        }

        [TestMethod]
        public async Task GetMetadata_WhenAutomaticRefresh_DoesNotCallAssist()
        {
            using var harness = CreateHarness(httpContext: LlmProviderFlowTestHelpers.CreateAutomaticRefreshHttpContext(), isAutomated: true, allowTextCompletion: true);
            harness.LlmService.EnqueueResult(CreateSuccessResult(title: "启程"));

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, harness.LlmService.Requests.Count);
            Assert.AreEqual("第 1 集", result.Item!.Name);
        }

        [TestMethod]
        public async Task GetMetadata_WhenEpisodeIsMissing_DoesNotCallAssist()
        {
            using var harness = CreateHarness(httpContext: LlmProviderFlowTestHelpers.CreateExplicitRefreshHttpContext(TestItemIdString()), isMissingEpisode: true, allowTextCompletion: true);
            harness.LlmService.EnqueueResult(CreateSuccessResult(title: "启程"));

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, harness.LlmService.Requests.Count);
            Assert.IsFalse(result.HasMetadata);
        }

        [TestMethod]
        public async Task GetMetadata_WhenManualMatch_AllowsLlmAssistAndKeepsProviderIds()
        {
            using var harness = CreateHarness(httpContext: LlmProviderFlowTestHelpers.CreateManualMatchHttpContext(TestItemIdString()), allowTextCompletion: true);
            var providerIdsSnapshot = LlmProviderFlowTestHelpers.CloneProviderIds(harness.Info.SeriesProviderIds);
            harness.LlmService.EnqueueResult(CreateSuccessResult(title: "启程", overview: "雫第一次参加神之水滴选拔挑战。"));

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, harness.LlmService.Requests.Count);
            Assert.AreEqual("启程", result.Item!.Name);
            Assert.AreEqual("雫第一次参加神之水滴选拔挑战。", result.Item.Overview);
            AssertSafeEpisodeLlmRequest(harness.LlmService.Requests.Single(), harness.LlmService.LastResult);
            LlmProviderFlowTestHelpers.AssertProviderIdsUnchanged(providerIdsSnapshot, harness.Info.SeriesProviderIds, "SeriesProviderIds");
        }

        [TestMethod]
        public async Task GetMetadata_WhenExplicitSearchMissing_AllowsLlmTitleAndQueuesBackfillCandidateWithLocalItemPath()
        {
            using var harness = CreateHarness(httpContext: LlmProviderFlowTestHelpers.CreateExplicitSearchMissingHttpContext(TestItemIdString()), allowTextCompletion: true);
            harness.LlmService.EnqueueResult(CreateSuccessResult(title: "启程"));

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);
            var candidate = harness.TitleCandidateStore.Peek(harness.Episode.Id);

            Assert.AreEqual(1, harness.LlmService.Requests.Count);
            Assert.AreEqual("启程", result.Item!.Name);
            Assert.IsNotNull(candidate);
            Assert.AreEqual("启程", candidate!.CandidateTitle);
            Assert.AreEqual(harness.Info.Path, candidate.ItemPath);
            Assert.IsTrue(Path.IsPathRooted(candidate.ItemPath), "本地 item path 只应留在 store，不应进入 LLM prompt。 ");
            AssertSafeEpisodeLlmRequest(harness.LlmService.Requests.Single(), harness.LlmService.LastResult);
        }

        [TestMethod]
        public async Task GetMetadata_WhenImplicitScheduledFallback_DoesNotCallAssistButKeepsExistingFallbackCandidateBehavior()
        {
            using var harness = CreateHarness(httpContext: null, currentEpisodeTitle: "第 1 集", tmdbEpisodeName: "皇后回宫", allowTextCompletion: true);
            harness.LlmService.EnqueueResult(CreateSuccessResult(title: "启程"));

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);
            var candidate = harness.TitleCandidateStore.Peek(harness.Episode.Id);

            Assert.AreEqual(0, harness.LlmService.Requests.Count);
            Assert.AreEqual("皇后回宫", result.Item!.Name);
            Assert.IsNotNull(candidate, "隐式 scheduled fallback 仍应沿用既有 title backfill 兼容语义。 ");
            Assert.AreEqual("皇后回宫", candidate!.CandidateTitle);
        }

        [TestMethod]
        public async Task GetMetadata_WhenTextCompletionDisabled_DoesNotUseLlmText()
        {
            using var harness = CreateHarness(httpContext: LlmProviderFlowTestHelpers.CreateExplicitSearchMissingHttpContext(TestItemIdString()), allowTextCompletion: false);
            harness.LlmService.EnqueueResult(CreateSuccessResult(title: "启程", overview: "雫第一次参加神之水滴选拔挑战。"));

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, harness.LlmService.Requests.Count);
            Assert.AreEqual("第 1 集", result.Item!.Name);
            Assert.IsNull(result.Item.Overview);
        }

        [TestMethod]
        public async Task GetMetadata_WhenProviderTitleIsSpecific_DoesNotReplaceItWithLlmTitle()
        {
            using var harness = CreateHarness(httpContext: LlmProviderFlowTestHelpers.CreateExplicitRefreshHttpContext(TestItemIdString()), tmdbEpisodeName: "皇后回宫", allowTextCompletion: true);
            harness.LlmService.EnqueueResult(CreateSuccessResult(title: "启程"));

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, harness.LlmService.Requests.Count);
            Assert.AreEqual("皇后回宫", result.Item!.Name);
        }

        [TestMethod]
        public async Task GetMetadata_WhenLlmConfidenceIsLow_DoesNotUseLlmText()
        {
            using var harness = CreateHarness(httpContext: LlmProviderFlowTestHelpers.CreateExplicitSearchMissingHttpContext(TestItemIdString()), allowTextCompletion: true);
            harness.LlmService.EnqueueResult(CreateSuccessResult(title: "启程", confidence: 0.5));

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, harness.LlmService.Requests.Count);
            Assert.AreEqual("第 1 集", result.Item!.Name);
            Assert.IsNull(harness.TitleCandidateStore.Peek(harness.Episode.Id));
        }

        [TestMethod]
        public async Task GetMetadata_WhenLlmOverviewDiffersFromParents_AcceptsOverview()
        {
            using var harness = CreateHarness(
                httpContext: LlmProviderFlowTestHelpers.CreateExplicitRefreshHttpContext(TestItemIdString()),
                allowTextCompletion: true,
                seriesOverview: "世界级的葡萄酒评论家神咲丰多香辞世后，留下了一批葡萄酒收藏。",
                seasonOverview: "神咲丰多香的遗嘱引发了围绕梦幻葡萄酒的争夺。");
            harness.LlmService.EnqueueResult(CreateSuccessResult(overview: "雫第一次参加神之水滴选拔挑战。"));

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("雫第一次参加神之水滴选拔挑战。", result.Item!.Overview);
            Assert.AreEqual("zh-CN", result.ResultLanguage);
        }

        [TestMethod]
        public async Task GetMetadata_WhenLlmOverviewDuplicatesSeriesOverview_RejectsOverviewAndQueuesCleanupCandidate()
        {
            var parentOverview = "世界级的葡萄酒评论家神咲丰多香辞世后，留下了一批葡萄酒收藏。";
            using var harness = CreateHarness(
                httpContext: LlmProviderFlowTestHelpers.CreateExplicitSearchMissingHttpContext(TestItemIdString()),
                allowTextCompletion: true,
                seriesOverview: parentOverview,
                seasonOverview: "神咲丰多香的遗嘱引发了围绕梦幻葡萄酒的争夺。");
            harness.LlmService.EnqueueResult(CreateSuccessResult(overview: parentOverview));

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);
            var candidate = harness.OverviewCleanupCandidateStore.Peek(harness.Episode.Id);

            Assert.IsNull(result.Item!.Overview);
            Assert.IsNotNull(candidate, "LLM 简介被父级去重拒绝后，应继续沿用 cleanup candidate 语义。 ");
            Assert.AreEqual(harness.Info.Path, candidate!.ItemPath);
        }

        [TestMethod]
        public async Task GetMetadata_WhenLlmSuggestsDifferentSeasonEpisode_DoesNotChangeNumberingOrProviderIds()
        {
            using var harness = CreateHarness(httpContext: LlmProviderFlowTestHelpers.CreateManualMatchHttpContext(TestItemIdString()), allowTextCompletion: true);
            var providerIdsSnapshot = LlmProviderFlowTestHelpers.CloneProviderIds(harness.Info.SeriesProviderIds);
            harness.LlmService.EnqueueResult(CreateSuccessResult(title: "启程", seasonNumber: 2, episodeNumber: 5));

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, result.Item!.ParentIndexNumber);
            Assert.AreEqual(1, result.Item.IndexNumber);
            Assert.AreEqual(1, harness.Info.ParentIndexNumber);
            Assert.AreEqual(1, harness.Info.IndexNumber);
            LlmProviderFlowTestHelpers.AssertProviderIdsUnchanged(providerIdsSnapshot, harness.Info.SeriesProviderIds, "SeriesProviderIds");
        }

        [TestMethod]
        public async Task GetMetadata_WhenTvdbSpecialPlacementIsEmpty_LlmDoesNotCreatePlacementFields()
        {
            using var harness = CreateHarness(
                httpContext: LlmProviderFlowTestHelpers.CreateManualMatchHttpContext(TestItemIdString()),
                parentIndexNumber: 0,
                indexNumber: 1,
                seriesProviderIds: new Dictionary<string, string>
                {
                    [MetadataProvider.Tmdb.ToString()] = "123",
                    [MetadataProvider.Tvdb.ToString()] = "321",
                },
                allowTextCompletion: true);
            harness.LlmService.EnqueueResult(CreateSuccessResult(title: "特别篇", seasonNumber: 7, episodeNumber: 9));

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, result.Item!.ParentIndexNumber);
            Assert.AreEqual(1, result.Item.IndexNumber);
            Assert.IsNull(result.Item.AirsBeforeSeasonNumber);
            Assert.IsNull(result.Item.AirsBeforeEpisodeNumber);
            Assert.IsNull(result.Item.AirsAfterSeasonNumber);
        }

        [TestMethod]
        public async Task GetMetadata_WhenLlmFails_KeepsExistingEpisodeFlow()
        {
            using var harness = CreateHarness(httpContext: LlmProviderFlowTestHelpers.CreateExplicitSearchMissingHttpContext(TestItemIdString()), tmdbEpisodeName: "皇后回宫", allowTextCompletion: true);
            harness.LlmService.EnqueueResult(LlmScrapingAssistResult.Failed("schema invalid", CreateContext()));

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);
            var candidate = harness.TitleCandidateStore.Peek(harness.Episode.Id);

            Assert.AreEqual(1, harness.LlmService.Requests.Count);
            Assert.AreEqual("皇后回宫", result.Item!.Name);
            Assert.IsNotNull(candidate);
            Assert.AreEqual("皇后回宫", candidate!.CandidateTitle);
        }

        [TestMethod]
        public async Task GetMetadata_WhenManualMatch_InvokesEpisodeGroupMappingAssistAndQueuesRefresh()
        {
            var mappedSeries = new Series
            {
                Id = Guid.NewGuid(),
                Name = "Series A",
                ProviderIds = new Dictionary<string, string>
                {
                    [MetadataProvider.Tmdb.ToString()] = "123",
                },
            };
            using var harness = CreateHarness(httpContext: LlmProviderFlowTestHelpers.CreateManualMatchHttpContext(TestItemIdString()), allowEpisodeGroupMappingAssist: true, seriesForRefresh: mappedSeries);
            ExplicitEpisodeGroupMappingTestHelper.SeedEpisodeGroupById(harness.TmdbApi, "candidate-group", "zh-CN");
            harness.LlmEpisodeGroupMappingApi.Enqueue("candidate-group", 0.95);

            _ = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, harness.LlmEpisodeGroupMappingApi.Prompts.Count);
            Assert.AreEqual("123=candidate-group", MetaSharkPlugin.Instance!.Configuration.TmdbEpisodeGroupMap);
            AssertQueuedSeries(harness.QueueRefreshCalls, mappedSeries.Id);
            var prompt = harness.LlmEpisodeGroupMappingApi.Prompts.Single();
            LlmProviderFlowTestHelpers.AssertNoSensitiveContent(prompt);
            StringAssert.Contains(prompt, "TV/Series A/Season 01/S01E01.mkv");
        }

        [TestMethod]
        public async Task GetMetadata_WhenDirectEpisodeMissing_LlmGroupMappingCanResolveCurrentRequest()
        {
            var mappedSeries = new Series
            {
                Id = Guid.NewGuid(),
                Name = "Series A",
                ProviderIds = new Dictionary<string, string>
                {
                    [MetadataProvider.Tmdb.ToString()] = "123",
                },
            };
            using var harness = CreateHarness(
                httpContext: LlmProviderFlowTestHelpers.CreateManualMatchHttpContext(TestItemIdString()),
                tmdbEpisodeName: null,
                parentIndexNumber: 2,
                indexNumber: 1,
                allowEpisodeGroupMappingAssist: true,
                seriesForRefresh: mappedSeries);
            SeedEpisode(harness.TmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode
            {
                Name = "映射后命中的正片",
                Overview = "通过剧集组映射命中第一季第一集。",
                VoteAverage = 7.6,
                AirDate = new DateTime(2024, 2, 3),
            });
            ExplicitEpisodeGroupMappingTestHelper.SeedEpisodeGroupById(
                harness.TmdbApi,
                "candidate-group",
                "zh-CN",
                ExplicitEpisodeGroupMappingTestHelper.CreateGroup(
                    order: 2,
                    name: "映射第二季",
                    ExplicitEpisodeGroupMappingTestHelper.CreateEpisode(order: 0, seasonNumber: 1, episodeNumber: 1)));
            harness.LlmEpisodeGroupMappingApi.Enqueue("candidate-group", 0.95);

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, harness.LlmEpisodeGroupMappingApi.Prompts.Count);
            Assert.AreEqual("123=candidate-group", MetaSharkPlugin.Instance!.Configuration.TmdbEpisodeGroupMap);
            Assert.IsTrue(result.HasMetadata);
            Assert.AreEqual("映射后命中的正片", result.Item!.Name);
            Assert.AreEqual(2, result.Item.ParentIndexNumber);
            Assert.AreEqual(1, result.Item.IndexNumber);
            AssertQueuedSeries(harness.QueueRefreshCalls, mappedSeries.Id);
            var prompt = harness.LlmEpisodeGroupMappingApi.Prompts.Single();
            LlmProviderFlowTestHelpers.AssertNoSensitiveContent(prompt);
            StringAssert.Contains(prompt, "TV/Series A/Season 02/S02E01.mkv");
        }

        [TestMethod]
        public async Task GetMetadata_WhenAutomaticRefresh_DoesNotInvokeEpisodeGroupMappingAssist()
        {
            using var harness = CreateHarness(httpContext: LlmProviderFlowTestHelpers.CreateAutomaticRefreshHttpContext(), isAutomated: true, allowEpisodeGroupMappingAssist: true);
            harness.LlmEpisodeGroupMappingApi.Enqueue("candidate-group", 0.95);

            _ = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(0, harness.LlmEpisodeGroupMappingApi.Prompts.Count);
            Assert.AreEqual(string.Empty, MetaSharkPlugin.Instance!.Configuration.TmdbEpisodeGroupMap);
            Assert.AreEqual(0, harness.QueueRefreshCalls.Count);
        }

        private static FlowHarness CreateHarness(
            HttpContext? httpContext,
            bool enableLlm = true,
            bool allowTextCompletion = false,
            bool isAutomated = false,
            bool isMissingEpisode = false,
            string currentEpisodeTitle = "第 1 集",
            string? tmdbEpisodeName = "Episode 1",
            string? tmdbEpisodeOverview = null,
            string? translationOverview = null,
            string? seriesOverview = null,
            string? seasonOverview = null,
            int parentIndexNumber = 1,
            int indexNumber = 1,
            Dictionary<string, string>? seriesProviderIds = null,
            bool allowEpisodeGroupMappingAssist = false,
            Series? seriesForRefresh = null)
        {
            EnsurePluginInstance();
            var configuration = MetaSharkPlugin.Instance!.Configuration;
            configuration.EnableLlmAssist = enableLlm;
            configuration.EnableLlmEpisodeGroupMappingAssist = allowEpisodeGroupMappingAssist;
            configuration.LlmAllowTextCompletion = allowTextCompletion;
            configuration.LlmBaseUrl = enableLlm ? "http://127.0.0.1:11434/v1" : string.Empty;
            configuration.LlmModel = enableLlm ? "test-model" : string.Empty;
            configuration.LlmApiKey = enableLlm ? "sk-test-secret" : string.Empty;
            configuration.LlmConfidenceThreshold = 0.75;
            configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;
            configuration.EnableTvdbSpecialsWithinSeasons = true;
            configuration.TmdbEpisodeGroupMap = string.Empty;
            configuration.TvdbApiKey = string.Empty;
            configuration.TvdbHost = string.Empty;

            return new FlowHarness(
                httpContext,
                isAutomated,
                isMissingEpisode,
                currentEpisodeTitle,
                tmdbEpisodeName,
                tmdbEpisodeOverview,
                translationOverview,
                seriesOverview,
                seasonOverview,
                parentIndexNumber,
                indexNumber,
                seriesProviderIds,
                seriesForRefresh,
                LoggerFactory.Create(builder => { }));
        }

        private static string TestItemIdString()
        {
            return "11111111-1111-1111-1111-111111111111";
        }

        private static LlmScrapingAssistResult CreateSuccessResult(string? title = null, string? overview = null, double confidence = 0.9, int? seasonNumber = null, int? episodeNumber = null)
        {
            return LlmScrapingAssistResult.Succeeded(
                CreateContext(),
                new LlmScrapingSuggestion
                {
                    MediaType = "Episode",
                    Title = title,
                    Overview = overview,
                    SeasonNumber = seasonNumber,
                    EpisodeNumber = episodeNumber,
                    Confidence = confidence,
                },
                new LlmSearchHints { Title = title });
        }

        private static LlmPromptContext CreateContext()
        {
            return new LlmPromptContext
            {
                MediaType = "Episode",
                RelativePath = "TV/Series A/Season 01/S01E01.mkv",
                FileName = "S01E01.mkv",
                SeasonNumber = 1,
                EpisodeNumber = 1,
            };
        }

        private static void AssertSafeEpisodeLlmRequest(LlmScrapingAssistRequest request, LlmScrapingAssistResult? result)
        {
            Assert.IsInstanceOfType(request.LookupInfo, typeof(EpisodeInfo));
            var lookup = (EpisodeInfo)request.LookupInfo!;
            Assert.AreEqual("Episode", request.MediaType);
            Assert.AreEqual("TV/Series A/Season 01/S01E01.mkv", lookup.Path);
            Assert.AreEqual("第 1 集", lookup.Name);
            Assert.AreEqual(1, lookup.ParentIndexNumber);
            Assert.AreEqual(1, lookup.IndexNumber);
            StringAssert.Contains(lookup.SeriesDisplayOrder, "GenericTitle=True");
            StringAssert.Contains(lookup.SeriesDisplayOrder, "ProviderTitleState=present");
            StringAssert.Contains(lookup.SeriesDisplayOrder, "ExternalCandidateSummary=TMDb");
            Assert.AreEqual("present", lookup.SeriesProviderIds[MetadataProvider.Tmdb.ToString()]);
            Assert.IsFalse(lookup.SeriesProviderIds.ContainsValue("123"));
            LlmProviderFlowTestHelpers.AssertNoSensitiveContent(request, result, lookup.Path, lookup.Name, lookup.SeriesDisplayOrder);
        }

        private static EpisodeProvider CreateProvider(
            ILibraryManager libraryManager,
            IHttpContextAccessor httpContextAccessor,
            TmdbApi tmdbApi,
            TvdbApi tvdbApi,
            IEpisodeTitleBackfillCandidateStore titleBackfillCandidateStore,
            IEpisodeOverviewCleanupCandidateStore overviewCleanupCandidateStore,
            ILlmMetadataAssistService llmMetadataAssistService,
            ILlmEpisodeGroupMappingProviderAssistService llmEpisodeGroupMappingProviderAssistService,
            ILoggerFactory loggerFactory)
        {
            return new EpisodeProvider(
                new DefaultHttpClientFactory(),
                loggerFactory,
                libraryManager,
                httpContextAccessor,
                new DoubanApi(loggerFactory),
                tmdbApi,
                new OmdbApi(loggerFactory),
                new ImdbApi(loggerFactory),
                tvdbApi,
                titleBackfillCandidateStore,
                overviewCleanupCandidateStore,
                llmMetadataAssistService,
                llmEpisodeGroupMappingProviderAssistService);
        }

        private static EpisodeInfo CreateEpisodeInfo(int parentIndexNumber, int indexNumber, bool isAutomated, bool isMissingEpisode, Dictionary<string, string>? seriesProviderIds)
        {
            var pathSeasonNumber = parentIndexNumber < 0 ? 1 : parentIndexNumber;
            var pathEpisodeNumber = indexNumber <= 0 ? 1 : indexNumber;
            return new EpisodeInfo
            {
                Name = "第 1 集",
                Path = $"/mnt/media/TV/Series A/Season {pathSeasonNumber:00}/S{pathSeasonNumber:00}E{pathEpisodeNumber:00}.mkv",
                MetadataLanguage = "zh-CN",
                ParentIndexNumber = parentIndexNumber,
                IndexNumber = indexNumber,
                IsAutomated = isAutomated,
                IsMissingEpisode = isMissingEpisode,
                SeriesDisplayOrder = string.Empty,
                SeriesProviderIds = seriesProviderIds ?? new Dictionary<string, string>
                {
                    [MetadataProvider.Tmdb.ToString()] = "123",
                },
                ProviderIds = new Dictionary<string, string>
                {
                    [MetadataProvider.Tmdb.ToString()] = "episode-tmdb-id",
                },
            };
        }

        private static void SeedEpisode(TmdbApi tmdbApi, int seriesTmdbId, int seasonNumber, int episodeNumber, string language, string imageLanguages, TvEpisode episode)
        {
            var cacheField = typeof(TmdbApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(cacheField, "TmdbApi.memoryCache 未找到");

            var cache = cacheField!.GetValue(tmdbApi) as MemoryCache;
            Assert.IsNotNull(cache, "TmdbApi.memoryCache 不是有效的 MemoryCache");

            var key = $"episode-{seriesTmdbId}-s{seasonNumber}e{episodeNumber}-{language}-{imageLanguages}";
            cache!.Set(key, episode);
        }

        private static void SeedEpisodeTranslationOverview(TmdbApi tmdbApi, int seriesTmdbId, int seasonNumber, int episodeNumber, string language, string? overview)
        {
            var cacheField = typeof(TmdbApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(cacheField, "TmdbApi.memoryCache 未找到");

            var cache = cacheField!.GetValue(tmdbApi) as MemoryCache;
            Assert.IsNotNull(cache, "TmdbApi.memoryCache 不是有效的 MemoryCache");

            var key = $"episode-translation-overview-{seriesTmdbId}-s{seasonNumber}e{episodeNumber}-{language}";
            cache!.Set(
                key,
                overview == null
                    ? null
                    : new EpisodeLocalizedValue
                    {
                        Value = overview,
                        SourceLanguage = language,
                    });
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

            var configuration = new PluginConfiguration();
            var currentType = plugin.GetType();
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

        private sealed class FlowHarness : IDisposable
        {
            public FlowHarness(
                HttpContext? httpContext,
                bool isAutomated,
                bool isMissingEpisode,
                string currentEpisodeTitle,
                string? tmdbEpisodeName,
                string? tmdbEpisodeOverview,
                string? translationOverview,
                string? seriesOverview,
                string? seasonOverview,
                int parentIndexNumber,
                int indexNumber,
                Dictionary<string, string>? seriesProviderIds,
                Series? seriesForRefresh,
                ILoggerFactory loggerFactory)
            {
                this.TitleCandidateStore = new InMemoryEpisodeTitleBackfillCandidateStore();
                this.OverviewCleanupCandidateStore = new InMemoryEpisodeOverviewCleanupCandidateStore();
                this.LlmService = new RecordingLlmService();
                this.LlmEpisodeGroupMappingApi = new RecordingEpisodeGroupMappingLlmApi();
                this.QueueRefreshCalls = new List<QueueRefreshCall>();
                this.Info = CreateEpisodeInfo(parentIndexNumber, indexNumber, isAutomated, isMissingEpisode, seriesProviderIds);
                this.Episode = new Episode
                {
                    Id = Guid.NewGuid(),
                    Name = currentEpisodeTitle,
                    Path = this.Info.Path,
                };

                this.LibraryManagerStub = new Mock<ILibraryManager>();
                this.LibraryManagerStub.Setup(x => x.FindByPath(this.Info.Path, false)).Returns(this.Episode);
                this.LibraryManagerStub.Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(seriesForRefresh == null ? new List<BaseItem>() : new List<BaseItem> { seriesForRefresh });
                if (!string.IsNullOrWhiteSpace(seriesOverview) || !string.IsNullOrWhiteSpace(seasonOverview))
                {
                    var seasonPath = Path.GetDirectoryName(this.Info.Path)!;
                    var seriesPath = Path.GetDirectoryName(seasonPath)!;
                    var season = new Season { Path = seasonPath, Overview = seasonOverview };
                    var series = new Series { Id = Guid.NewGuid(), Path = seriesPath, Name = "Series A", Overview = seriesOverview };
                    this.LibraryManagerStub.Setup(x => x.FindByPath(seasonPath, true)).Returns(season);
                    this.LibraryManagerStub.Setup(x => x.FindByPath(seriesPath, true)).Returns(series);
                }

                this.HttpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
                var tmdbApi = new TmdbApi(loggerFactory);
                this.TmdbApi = tmdbApi;
                SeedSeriesEpisodeGroupCandidate(tmdbApi, this.Info.SeriesProviderIds[MetadataProvider.Tmdb.ToString()], this.Info.MetadataLanguage, "candidate-group");
                if (tmdbEpisodeName != null || tmdbEpisodeOverview != null)
                {
                    SeedEpisode(tmdbApi, 123, parentIndexNumber, indexNumber, "zh-CN", "zh-CN", new TvEpisode
                    {
                        Name = tmdbEpisodeName,
                        Overview = tmdbEpisodeOverview,
                        VoteAverage = 8.2,
                        AirDate = new DateTime(2024, 1, 1),
                    });
                }

                SeedEpisodeTranslationOverview(tmdbApi, 123, parentIndexNumber, indexNumber, "zh-CN", translationOverview);

                this.Provider = CreateProvider(
                    this.LibraryManagerStub.Object,
                    this.HttpContextAccessor,
                    tmdbApi,
                    new TvdbApi(loggerFactory),
                    this.TitleCandidateStore,
                    this.OverviewCleanupCandidateStore,
                    this.LlmService,
                    this.CreateEpisodeGroupMappingProviderAssistService(loggerFactory, tmdbApi),
                    loggerFactory);
            }

            public InMemoryEpisodeTitleBackfillCandidateStore TitleCandidateStore { get; }

            public InMemoryEpisodeOverviewCleanupCandidateStore OverviewCleanupCandidateStore { get; }

            public RecordingLlmService LlmService { get; }

            public RecordingEpisodeGroupMappingLlmApi LlmEpisodeGroupMappingApi { get; }

            public List<QueueRefreshCall> QueueRefreshCalls { get; }

            public TmdbApi TmdbApi { get; }

            public EpisodeInfo Info { get; }

            public Episode Episode { get; }

            public IHttpContextAccessor HttpContextAccessor { get; }

            public Mock<ILibraryManager> LibraryManagerStub { get; }

            public EpisodeProvider Provider { get; }

            private LlmEpisodeGroupMappingProviderAssistService CreateEpisodeGroupMappingProviderAssistService(ILoggerFactory loggerFactory, TmdbApi tmdbApi)
            {
                var providerManagerStub = new Mock<IProviderManager>();
                providerManagerStub
                    .Setup(x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()))
                    .Callback<Guid, MetadataRefreshOptions, RefreshPriority>((itemId, options, priority) => this.QueueRefreshCalls.Add(new QueueRefreshCall(itemId, options, priority)));

                return new LlmEpisodeGroupMappingProviderAssistService(
                    new LlmEpisodeGroupMappingAssistService(this.LlmEpisodeGroupMappingApi, tmdbApi),
                    tmdbApi,
                    this.LibraryManagerStub.Object,
                    providerManagerStub.Object,
                    Mock.Of<IFileSystem>(),
                    new LlmAssistTriggerPolicy(),
                    new EpisodeGroupRefreshService(),
                    loggerFactory.CreateLogger<LlmEpisodeGroupMappingProviderAssistService>());
            }

            public void Dispose()
            {
                this.Provider.Dispose();
            }
        }

        private sealed class RecordingLlmService : ILlmMetadataAssistService
        {
            private readonly LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService inner = new LlmProviderFlowTestHelpers.RecordingLlmMetadataAssistService();

            public List<LlmScrapingAssistRequest> Requests => this.inner.Requests;

            public LlmScrapingAssistResult? LastResult { get; private set; }

            public void EnqueueResult(LlmScrapingAssistResult result)
            {
                this.inner.EnqueueResult(result);
            }

            public async Task<LlmScrapingAssistResult> AssistAsync(LlmScrapingAssistRequest request, CancellationToken cancellationToken)
            {
                this.LastResult = await this.inner.AssistAsync(request, cancellationToken).ConfigureAwait(false);
                return this.LastResult;
            }
        }

        private static void SeedSeriesEpisodeGroupCandidate(TmdbApi tmdbApi, string seriesTmdbId, string language, string groupId)
        {
            var cacheField = typeof(TmdbApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(cacheField, "TmdbApi.memoryCache 未找到");

            var cache = cacheField!.GetValue(tmdbApi) as MemoryCache;
            Assert.IsNotNull(cache, "TmdbApi.memoryCache 不是有效的 MemoryCache");

            cache!.Set(
                $"series-{seriesTmdbId}-{language}-{language}",
                new TvShow
                {
                    Id = int.Parse(seriesTmdbId, System.Globalization.CultureInfo.InvariantCulture),
                    EpisodeGroups = new ResultContainer<TvGroupCollection>
                    {
                        Results = new List<TvGroupCollection>
                        {
                            new TvGroupCollection
                            {
                                Id = groupId,
                                Name = "候选剧集组",
                                Type = TvGroupType.Absolute,
                                GroupCount = 1,
                                EpisodeCount = 1,
                            },
                        },
                    },
                });
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

        private sealed record QueueRefreshCall(Guid ItemId, MetadataRefreshOptions Options, RefreshPriority Priority);

        private sealed class RecordingEpisodeGroupMappingLlmApi : ILlmApi
        {
            private readonly Queue<(string GroupId, double Confidence)> queuedResults = new Queue<(string GroupId, double Confidence)>();

            public List<string> Prompts { get; } = new List<string>();

            public void Enqueue(string groupId, double confidence)
            {
                this.queuedResults.Enqueue((groupId, confidence));
            }

            public Task<LlmApiResult> CompleteAsync(string prompt, CancellationToken cancellationToken)
            {
                this.Prompts.Add(prompt);
                var result = this.queuedResults.Count > 0 ? this.queuedResults.Dequeue() : (GroupId: "candidate-group", Confidence: 0.95);
                return Task.FromResult(LlmApiResult.Succeeded($"{{\"selectedGroupId\":\"{result.GroupId}\",\"confidence\":{result.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"reason\":\"test\"}}"));
            }
        }

    }
}
