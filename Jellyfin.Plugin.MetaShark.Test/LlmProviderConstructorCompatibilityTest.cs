using System;
using System.Net.Http;
using System.Reflection;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Providers.Llm;
using Jellyfin.Plugin.MetaShark.Workers;
using Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill;
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

        private static void AssertProviderLlmField(object provider, ILlmMetadataAssistService? expected)
        {
            var field = provider.GetType().GetField("llmMetadataAssistService", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"{provider.GetType().Name} 缺少 llmMetadataAssistService 私有字段。");
            Assert.AreSame(expected, field!.GetValue(provider));
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
