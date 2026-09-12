using System;
using System.Net.Http;
using System.Reflection;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Providers.Llm;
using Jellyfin.Plugin.MetaShark.Test.Logging;
using Jellyfin.Plugin.MetaShark.Workers;
using Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [TestCategory("Stable")]
    public class LlmProviderConstructorCompatibilityTest
    {
        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder => { });

        [TestMethod]
        public void Providers_ConstructWithoutLlmParameter_RemainSourceCompatible()
        {
            var context = new ConstructorContext(this.loggerFactory);

            var movieProvider = new MovieProvider(context.HttpClientFactory, context.LoggerFactory, context.LibraryManager, context.HttpContextAccessor, context.DoubanApi, context.TmdbApi, context.OmdbApi, context.ImdbApi);
            var seriesProvider = new SeriesProvider(context.HttpClientFactory, context.LoggerFactory, context.LibraryManager, context.HttpContextAccessor, context.DoubanApi, context.TmdbApi, context.OmdbApi, context.ImdbApi);
            var seasonProvider = new SeasonProvider(context.HttpClientFactory, context.LoggerFactory, context.LibraryManager, context.HttpContextAccessor, context.DoubanApi, context.TmdbApi, context.OmdbApi, context.ImdbApi);
            var episodeProvider = new EpisodeProvider(context.HttpClientFactory, context.LoggerFactory, context.LibraryManager, context.HttpContextAccessor, context.DoubanApi, context.TmdbApi, context.OmdbApi, context.ImdbApi, context.TvdbApi);

            AssertProviderLlmField(movieProvider, null);
            AssertProviderLlmField(seriesProvider, null);
            AssertProviderLlmField(seasonProvider, null);
            AssertProviderLlmField(episodeProvider, null);
        }

        [TestMethod]
        public void Providers_ConstructWithLlmParameter_SaveServiceWithoutInvokingIt()
        {
            var context = new ConstructorContext(this.loggerFactory);
            var llmService = new Mock<ILlmMetadataAssistService>(MockBehavior.Strict);

            var movieProvider = new MovieProvider(context.HttpClientFactory, context.LoggerFactory, context.LibraryManager, context.HttpContextAccessor, context.DoubanApi, context.TmdbApi, context.OmdbApi, context.ImdbApi, null, llmService.Object);
            var seriesProvider = new SeriesProvider(context.HttpClientFactory, context.LoggerFactory, context.LibraryManager, context.HttpContextAccessor, context.DoubanApi, context.TmdbApi, context.OmdbApi, context.ImdbApi, null, llmService.Object);
            var seasonProvider = new SeasonProvider(context.HttpClientFactory, context.LoggerFactory, context.LibraryManager, context.HttpContextAccessor, context.DoubanApi, context.TmdbApi, context.OmdbApi, context.ImdbApi, llmService.Object);
            var episodeProviderShort = new EpisodeProvider(context.HttpClientFactory, context.LoggerFactory, context.LibraryManager, context.HttpContextAccessor, context.DoubanApi, context.TmdbApi, context.OmdbApi, context.ImdbApi, context.TvdbApi, null, llmService.Object);
            var episodeProviderLong = new EpisodeProvider(context.HttpClientFactory, context.LoggerFactory, context.LibraryManager, context.HttpContextAccessor, context.DoubanApi, context.TmdbApi, context.OmdbApi, context.ImdbApi, context.TvdbApi, Mock.Of<IEpisodeTitleBackfillCandidateStore>(), Mock.Of<IEpisodeOverviewCleanupCandidateStore>(), llmService.Object);

            AssertProviderLlmField(movieProvider, llmService.Object);
            AssertProviderLlmField(seriesProvider, llmService.Object);
            AssertProviderLlmField(seasonProvider, llmService.Object);
            AssertProviderLlmField(episodeProviderShort, llmService.Object);
            AssertProviderLlmField(episodeProviderLong, llmService.Object);
            llmService.Verify(service => service.AssistAsync(It.IsAny<LlmScrapingAssistRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public void ProviderPreTriggerRejectedDecision_ShouldBeExplicitlyObservable()
        {
            var loggerStub = new Mock<ILogger<MovieProvider>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            LlmObservabilityLog.LogLlmAssistRejected(loggerStub.Object, "AutomaticRefreshRejected", "Movie", DefaultScraperSemantic.AutomaticRefresh, false);

            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Information,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["ReasonCode"] = "AutomaticRefreshRejected",
                    ["Accepted"] = false,
                    ["MediaType"] = "Movie",
                    ["Semantic"] = DefaultScraperSemantic.AutomaticRefresh.ToString(),
                    ["IsImageProvider"] = false,
                },
                originalFormatContains: "[MetaShark] LLM 触发已评估. reason={ReasonCode} accepted={Accepted} mediaType={MediaType} semantic={Semantic} imageProvider={IsImageProvider}",
                messageContains: ["LLM 触发已评估"]);
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Information,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["ReasonCode"] = "AutomaticRefreshRejected",
                    ["MediaType"] = "Movie",
                    ["Semantic"] = DefaultScraperSemantic.AutomaticRefresh.ToString(),
                    ["IsImageProvider"] = false,
                },
                originalFormatContains: "[MetaShark] LLM 触发已拒绝. reason={ReasonCode} mediaType={MediaType} semantic={Semantic} imageProvider={IsImageProvider}",
                messageContains: ["LLM 触发已拒绝"]);
        }

        [TestMethod]
        public void ProviderSource_ShouldLogEveryPreTriggerRejectionEarlyReturn()
        {
            var providerDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Jellyfin.Plugin.MetaShark/Providers"));
            var providerFiles = new[]
            {
                Path.Combine(providerDirectory, "MovieProvider.cs"),
                Path.Combine(providerDirectory, "SeriesProvider.cs"),
                Path.Combine(providerDirectory, "SeasonProvider.cs"),
                Path.Combine(providerDirectory, "EpisodeProvider.cs"),
            };

            foreach (var providerFile in providerFiles)
            {
                var text = File.ReadAllText(providerFile);
                var cursor = 0;
                while (true)
                {
                    var index = text.IndexOf("if (!triggerDecision.ShouldTrigger", cursor, StringComparison.Ordinal);
                    if (index < 0)
                    {
                        break;
                    }

                    var blockEnd = text.IndexOf("return ", index, StringComparison.Ordinal);
                    Assert.IsTrue(blockEnd > index, $"{Path.GetFileName(providerFile)} 的 triggerDecision 早退块缺少 return。Index={index}");
                    var block = text[index..blockEnd];
                    Assert.IsTrue(
                        block.Contains("LlmObservabilityLog.LogLlmAssistRejected", StringComparison.Ordinal)
                            || block.Contains("LlmObservabilityLog.LogTmdbCorrectionRejected", StringComparison.Ordinal),
                        $"{Path.GetFileName(providerFile)} 的 triggerDecision 拒绝早退缺少 provider 显式观测日志。Index={index}");
                    cursor = index + 1;
                }
            }
        }

        [TestMethod]
        public void ProviderSource_ShouldLogConfigurationAndServiceShortCircuits()
        {
            var providerDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Jellyfin.Plugin.MetaShark/Providers"));
            AssertSourceContainsLogBeforeReturn(
                Path.Combine(providerDirectory, "MovieProvider.cs"),
                "return LlmExternalIdResolutionResult.NotTriggered(\"LlmConfigurationMissing\");",
                "LlmObservabilityLog.LogLlmAssistRejected(this.Logger, \"LlmConfigurationMissing\", nameof(Movie), semantic, false);");
            AssertSourceContainsLogBeforeReturn(
                Path.Combine(providerDirectory, "SeriesProvider.cs"),
                "return LlmExternalIdResolutionResult.NotTriggered(\"LlmConfigurationMissing\");",
                "LlmObservabilityLog.LogLlmAssistRejected(this.Logger, \"LlmConfigurationMissing\", nameof(Series), semantic, false);");
            AssertSourceContainsLogBeforeReturn(
                Path.Combine(providerDirectory, "SeasonProvider.cs"),
                "return null;",
                "LlmObservabilityLog.LogLlmAssistRejected(this.Logger, \"LlmConfigurationMissing\", nameof(Season), semantic, false);");
            AssertSourceContainsLogBeforeReturn(
                Path.Combine(providerDirectory, "EpisodeProvider.cs"),
                "return LlmExternalIdResolutionResult.NotTriggered(\"LlmExternalIdResolutionServiceMissing\");",
                "LlmObservabilityLog.LogLlmAssistRejected(this.Logger, \"LlmExternalIdResolutionServiceMissing\", nameof(Episode), semantic, false);");
        }

        [TestMethod]
        public void ProviderSource_ShouldLogTmdbCorrectionShortCircuits()
        {
            var providerDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Jellyfin.Plugin.MetaShark/Providers"));
            AssertSourceContainsLogBeforeReturn(
                Path.Combine(providerDirectory, "MovieProvider.cs"),
                "return LlmTmdbIdCorrectionResult.NoReplacement(reason);",
                "LlmObservabilityLog.LogTmdbCorrectionRejected(this.Logger, reason, nameof(Movie), semantic, false);");
            AssertSourceContainsLogBeforeReturn(
                Path.Combine(providerDirectory, "SeriesProvider.cs"),
                "return LlmTmdbIdCorrectionResult.NoReplacement(reason);",
                "LlmObservabilityLog.LogTmdbCorrectionRejected(this.Logger, reason, nameof(Series), semantic, false);");
        }

        [TestMethod]
        public void ProviderPreTmdbCorrectionRejectedDecision_ShouldUseCorrectionObservability()
        {
            var loggerStub = new Mock<ILogger<SeriesProvider>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            LlmObservabilityLog.LogTmdbCorrectionRejected(loggerStub.Object, "ImplicitRefreshRejected", "Series", DefaultScraperSemantic.UserRefresh, false);

            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Information,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["ReasonCode"] = "ImplicitRefreshRejected",
                    ["MediaType"] = "Series",
                    ["Semantic"] = DefaultScraperSemantic.UserRefresh.ToString(),
                    ["IsImageProvider"] = false,
                },
                originalFormatContains: "[MetaShark] LLM TMDb 纠错已拒绝. reason={ReasonCode} mediaType={MediaType} semantic={Semantic} imageProvider={IsImageProvider}",
                messageContains: ["LLM TMDb 纠错已拒绝"]);
        }

        private static void AssertProviderLlmField(object provider, ILlmMetadataAssistService? expected)
        {
            var field = provider.GetType().GetField("llmMetadataAssistService", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"{provider.GetType().Name} 缺少 llmMetadataAssistService 私有字段。");
            Assert.AreSame(expected, field!.GetValue(provider));
        }

        private static void AssertSourceContainsLogBeforeReturn(string providerFile, string returnStatement, string expectedLogCall)
        {
            var text = File.ReadAllText(providerFile);
            var logIndex = text.IndexOf(expectedLogCall, StringComparison.Ordinal);
            Assert.IsTrue(logIndex >= 0, $"{Path.GetFileName(providerFile)} 在目标 return 前缺少观测日志：{expectedLogCall}");
            var returnIndex = text.IndexOf(returnStatement, logIndex, StringComparison.Ordinal);
            Assert.IsTrue(returnIndex > logIndex, $"{Path.GetFileName(providerFile)} 缺少目标 return：{returnStatement}");
        }

        private sealed class ConstructorContext
        {
            public ConstructorContext(ILoggerFactory loggerFactory)
            {
                this.LoggerFactory = loggerFactory;
                this.HttpClientFactory = Mock.Of<IHttpClientFactory>();
                this.LibraryManager = Mock.Of<ILibraryManager>();
                this.HttpContextAccessor = new HttpContextAccessor();
                this.DoubanApi = new DoubanApi(loggerFactory);
                this.TmdbApi = new TmdbApi(loggerFactory);
                this.OmdbApi = new OmdbApi(loggerFactory);
                this.ImdbApi = new ImdbApi(loggerFactory);
                this.TvdbApi = new TvdbApi(loggerFactory);
            }

            public IHttpClientFactory HttpClientFactory { get; }

            public ILoggerFactory LoggerFactory { get; }

            public ILibraryManager LibraryManager { get; }

            public IHttpContextAccessor HttpContextAccessor { get; }

            public DoubanApi DoubanApi { get; }

            public TmdbApi TmdbApi { get; }

            public OmdbApi OmdbApi { get; }

            public ImdbApi ImdbApi { get; }

            public TvdbApi TvdbApi { get; }
        }
    }
}
