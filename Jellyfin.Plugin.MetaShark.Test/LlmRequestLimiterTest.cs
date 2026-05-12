using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.EpisodeGroupMapping;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Providers.Llm;
using MediaBrowser.Controller.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [DoNotParallelize]
    public class LlmRequestLimiterTest
    {
        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder => { });

        [TestMethod]
        public async Task TryAcquireAsync_DefaultLimiter_AllowsOnlyOneConcurrentLease()
        {
            using var limiter = new LlmRequestLimiter();

            using var firstLease = await limiter.TryAcquireAsync(CancellationToken.None).ConfigureAwait(false);
            using var secondLease = await limiter.TryAcquireAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, limiter.MaxConcurrency);
            Assert.IsNotNull(firstLease);
            Assert.IsNull(secondLease);
        }

        [TestMethod]
        public async Task MetadataAssist_WhenLimiterBusy_ShouldSkipWithoutCallingApi()
        {
            using var limiter = new LlmRequestLimiter();
            using var heldLease = await limiter.TryAcquireAsync(CancellationToken.None).ConfigureAwait(false);
            var api = new RecordingLlmApi(LlmApiResult.Succeeded("{\"suggestions\":[]}"));
            var service = new LlmMetadataAssistService(
                api,
                new LlmAssistTriggerPolicy(),
                new LlmScrapeContextBuilder(),
                new LlmSuggestionValidator(),
                new LlmScrapeMismatchDetector(),
                new LlmMetadataMergePolicy(),
                limiter);

            var result = await service.AssistAsync(CreateMetadataRequest(), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmScrapingAssistStatus.Skipped, result.Status);
            Assert.AreEqual("LlmRequestLimiterBusy", result.Diagnostic);
            Assert.AreEqual(0, api.CallCount);
        }

        [TestMethod]
        public async Task ExternalIdResolution_WhenLimiterBusy_ShouldSkipWithoutCallingApi()
        {
            using var limiter = new LlmRequestLimiter();
            using var heldLease = await limiter.TryAcquireAsync(CancellationToken.None).ConfigureAwait(false);
            var api = new RecordingLlmApi(LlmApiResult.Succeeded("{\"externalIdCandidates\":[]}"));
            var service = new LlmExternalIdResolutionService(
                api,
                new TmdbApi(this.loggerFactory),
                new DoubanApi(this.loggerFactory),
                new TvdbApi(this.loggerFactory),
                new LlmAssistTriggerPolicy(),
                new LlmExternalIdCandidateValidator(),
                limiter);

            var result = await service.ResolveAsync(CreateExternalIdRequest(), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmExternalIdResolutionStatus.Skipped, result.Status);
            Assert.AreEqual("LlmRequestLimiterBusy", result.Diagnostic);
            Assert.AreEqual(0, api.CallCount);
        }

        [TestMethod]
        public async Task EpisodeGroupMappingAssist_WhenLimiterBusy_ShouldNoOpWithoutCallingApi()
        {
            using var limiter = new LlmRequestLimiter();
            using var heldLease = await limiter.TryAcquireAsync(CancellationToken.None).ConfigureAwait(false);
            var api = new RecordingLlmApi(LlmApiResult.Succeeded("{\"selectedGroupId\":\"candidate-group\",\"confidence\":0.9,\"reason\":\"match\"}"));
            var service = new LlmEpisodeGroupMappingAssistService(
                api,
                new TmdbApi(this.loggerFactory),
                EpisodeGroupMapParser.Shared,
                limiter);
            var configuration = CreateConfiguration();

            var result = await service.SuggestAndWriteAsync(CreateEpisodeGroupRequest(configuration), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(LlmEpisodeGroupMappingAssistStatus.NotTriggered, result.Status);
            Assert.AreEqual("LlmRequestLimiterBusy", result.Reason);
            Assert.AreEqual(string.Empty, configuration.LlmTmdbEpisodeGroupMap);
            Assert.AreEqual(0, api.CallCount);
        }

        private static LlmScrapingAssistRequest CreateMetadataRequest()
        {
            return new LlmScrapingAssistRequest
            {
                Configuration = CreateConfiguration(),
                LookupInfo = new MovieInfo { Name = "Inception", Year = 2010, MetadataLanguage = "zh-CN" },
                MediaType = "Movie",
                Semantic = DefaultScraperSemantic.UserRefresh,
                HttpContext = CreateRefreshContext(),
                LibraryRoots = new[] { "/mnt/media" },
            };
        }

        private static LlmExternalIdResolutionRequest CreateExternalIdRequest()
        {
            return new LlmExternalIdResolutionRequest
            {
                Configuration = CreateConfiguration(),
                LookupInfo = new MovieInfo { Name = "Inception", Year = 2010, MetadataLanguage = "zh-CN" },
                MediaType = "Movie",
                Semantic = DefaultScraperSemantic.UserRefresh,
                HttpContext = CreateRefreshContext(),
                LibraryRoots = new[] { "/mnt/media" },
            };
        }

        private static LlmEpisodeGroupMappingAssistRequest CreateEpisodeGroupRequest(PluginConfiguration configuration)
        {
            return new LlmEpisodeGroupMappingAssistRequest
            {
                Configuration = configuration,
                SeriesTmdbId = 65942,
                SeriesTitle = "测试剧集",
                CandidateGroups = new[]
                {
                    new LlmEpisodeGroupCandidate
                    {
                        GroupId = "candidate-group",
                        Name = "候选剧集组",
                        Type = "storyArc",
                        GroupCount = 1,
                        EpisodeCount = 12,
                    },
                },
                MetadataLanguage = "zh-CN",
                IsManualTrigger = true,
            };
        }

        private static PluginConfiguration CreateConfiguration()
        {
            return new PluginConfiguration
            {
                EnableLlmAssist = true,
                EnableLlmEpisodeGroupMappingAssist = true,
                LlmBaseUrl = "http://127.0.0.1:11434/v1",
                LlmModel = "test-model",
                LlmApiKey = "test-key",
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

        private sealed class RecordingLlmApi : ILlmApi
        {
            private readonly LlmApiResult result;

            public RecordingLlmApi(LlmApiResult result)
            {
                this.result = result;
            }

            public int CallCount { get; private set; }

            public Task<LlmApiResult> CompleteAsync(string prompt, CancellationToken cancellationToken)
            {
                this.CallCount++;
                return Task.FromResult(this.result);
            }
        }
    }
}
