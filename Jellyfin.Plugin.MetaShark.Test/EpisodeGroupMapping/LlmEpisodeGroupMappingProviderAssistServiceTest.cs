using System.Globalization;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.EpisodeGroupMapping;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Providers.Llm;
using Jellyfin.Plugin.MetaShark.Test.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using TMDbLib.Objects.General;
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.MetaShark.Test.EpisodeGroupMapping
{
    [TestClass]
    [TestCategory("Stable")]
    public class LlmEpisodeGroupMappingProviderAssistServiceTest
    {
        [TestMethod]
        public async Task SuggestWriteAndRefreshAsync_WhenAssistWritesMapping_ShouldLogStructuredResult()
        {
            var loggerStub = new Mock<ILogger<LlmEpisodeGroupMappingProviderAssistService>>();
            loggerStub.Setup(x => x.IsEnabled(LogLevel.Information)).Returns(true);
            using var loggerFactory = LoggerFactory.Create(builder => { });
            var tmdbApi = new TmdbApi(loggerFactory);
            SeedTmdbSeries(tmdbApi, 65942, "zh-CN", "candidate-group");
            ExplicitEpisodeGroupMappingTestHelper.SeedEpisodeGroupById(tmdbApi, "candidate-group", "zh-CN");
            var llmApi = new RecordingLlmApi("candidate-group", 0.95);
            var persistenceService = new RecordingPersistenceService();
            var providerManagerStub = new Mock<IProviderManager>();
            var service = new LlmEpisodeGroupMappingProviderAssistService(
                new LlmEpisodeGroupMappingAssistService(llmApi, tmdbApi, EpisodeGroupMapParser.Shared, persistenceService),
                tmdbApi,
                CreateLibraryManager(CreateSeries(Guid.NewGuid(), "Re:Zero", "65942")).Object,
                providerManagerStub.Object,
                Mock.Of<IFileSystem>(),
                new LlmAssistTriggerPolicy(),
                new EpisodeGroupRefreshService(),
                loggerStub.Object);
            var configuration = new PluginConfiguration
            {
                EnableLlmAssist = true,
                EnableLlmEpisodeGroupMappingAssist = true,
                LlmAllowTextCompletion = true,
                LlmBaseUrl = "http://localhost",
                LlmModel = "test-model",
                LlmApiKey = "test-key",
                LlmEpisodeGroupMappingMinConfidence = 0.8,
            };

            var result = await service.SuggestWriteAndRefreshAsync(
                    new LlmEpisodeGroupMappingProviderAssistRequest
                    {
                        Configuration = configuration,
                        SeriesTmdbId = 65942,
                        SeriesTitle = "Re:Zero",
                        MetadataLanguage = "zh-CN",
                        MediaType = nameof(Series),
                        Semantic = DefaultScraperSemantic.UserRefresh,
                        HttpContext = LlmProviderFlowTestHelpers.CreateExplicitRefreshHttpContext(Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture), replaceAllMetadata: true),
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);

            Assert.AreEqual(LlmEpisodeGroupMappingAssistStatus.Updated, result.Status);
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Information,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["Status"] = nameof(LlmEpisodeGroupMappingAssistStatus.Updated),
                    ["ReasonCode"] = "Updated",
                    ["SeriesTmdbId"] = "65942",
                    ["SelectedGroupId"] = "candidate-group",
                    ["CandidateCount"] = 1,
                    ["WroteMapping"] = true,
                },
                originalFormatContains: "LLM 剧集组映射辅助完成",
                messageContains: ["LLM 剧集组映射辅助完成", "status=Updated", "seriesTmdbId=65942"]);
        }

        private static Mock<ILibraryManager> CreateLibraryManager(BaseItem item)
        {
            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { item });
            return libraryManagerStub;
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

        private static void SeedTmdbSeries(TmdbApi tmdbApi, int tmdbId, string language, string groupId)
        {
            GetTmdbMemoryCache(tmdbApi).Set(
                $"series-{tmdbId.ToString(CultureInfo.InvariantCulture)}-{language}-{language}",
                new TvShow
                {
                    Id = tmdbId,
                    Name = "Re:Zero",
                    EpisodeGroups = new ResultContainer<TvGroupCollection>
                    {
                        Results = new List<TvGroupCollection>
                        {
                            new TvGroupCollection
                            {
                                Id = groupId,
                                Name = "Absolute",
                                Type = TvGroupType.Absolute,
                                GroupCount = 1,
                                EpisodeCount = 1,
                            },
                        },
                    },
                },
                TimeSpan.FromMinutes(5));
        }

        private static MemoryCache GetTmdbMemoryCache(TmdbApi tmdbApi)
        {
            var memoryCacheField = typeof(TmdbApi).GetField("memoryCache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(memoryCacheField, "TmdbApi.memoryCache 未找到");
            var memoryCache = memoryCacheField!.GetValue(tmdbApi) as MemoryCache;
            Assert.IsNotNull(memoryCache, "TmdbApi.memoryCache 不是有效的 MemoryCache");
            return memoryCache!;
        }

        private sealed class RecordingLlmApi : ILlmApi
        {
            private readonly string selectedGroupId;
            private readonly double confidence;

            public RecordingLlmApi(string selectedGroupId, double confidence)
            {
                this.selectedGroupId = selectedGroupId;
                this.confidence = confidence;
            }

            public Task<LlmApiResult> CompleteAsync(string prompt, CancellationToken cancellationToken)
            {
                return Task.FromResult(LlmApiResult.Succeeded($"{{\"selectedGroupId\":\"{this.selectedGroupId}\",\"confidence\":{this.confidence.ToString(CultureInfo.InvariantCulture)},\"reason\":\"test\"}}"));
            }
        }

        private sealed class RecordingPersistenceService : ITmdbEpisodeGroupMapPersistenceService
        {
            public Task<TmdbEpisodeGroupMapPersistenceResult> TrySaveAsync(string? expectedOldMapping, string? newMapping, CancellationToken cancellationToken)
            {
                return Task.FromResult(TmdbEpisodeGroupMapPersistenceResult.SavedResult(expectedOldMapping ?? string.Empty, newMapping ?? string.Empty));
            }
        }
    }
}
