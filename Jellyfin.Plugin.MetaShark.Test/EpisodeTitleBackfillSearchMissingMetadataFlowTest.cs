using Jellyfin.Plugin.MetaShark;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using System.Reflection;
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class EpisodeTitleBackfillSearchMissingMetadataFlowTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-episode-title-backfill-flow-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");

        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder => { });

        [TestCleanup]
        public void Cleanup()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = false;
        }

        [TestMethod]
        public async Task SearchMissingMetadataFlow_MetadataDownloadWithQueuedCandidate_PersistsAndBackfillsEpisodeTitle()
        {
            using var harness = CreateHarness(featureEnabled: true, metadataRefreshMode: "FullRefresh", replaceAllMetadata: "false");

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);
            var queuedCandidate = harness.CandidateStore.Peek(harness.Episode.Id);

            await harness.TriggerItemUpdatedAsync(ItemUpdateType.MetadataDownload).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual("皇后回宫", result.Item!.Name);
            Assert.IsNotNull(queuedCandidate, "provider 应为搜索缺失元数据链路保存 candidate。");
            Assert.AreEqual(harness.Episode.Id, queuedCandidate!.ItemId);
            Assert.AreEqual("第 1 集", queuedCandidate.OriginalTitleSnapshot);
            Assert.AreEqual("皇后回宫", queuedCandidate.CandidateTitle);
            Assert.AreEqual(1, harness.Persistence.SaveCallCount, "MetadataDownload 事件应驱动一次持久化。");
            Assert.AreSame(harness.Episode, harness.Persistence.SavedEpisodes.Single(), "应持久化实际宿主 Episode 实例。");
            Assert.AreEqual("皇后回宫", harness.Persistence.SavedEpisodes.Single().Name);
            Assert.AreEqual("皇后回宫", harness.Episode.Name, "最终宿主 Episode 标题应被回填为 candidate title。");
            Assert.IsNull(harness.CandidateStore.Peek(harness.Episode.Id), "成功持久化后 candidate 应被移除。");
        }

        [TestMethod]
        public async Task SearchMissingMetadataFlow_ReplaceAllMetadataTrue_DoesNotQueueOrApplyBackfill()
        {
            using var harness = CreateHarness(featureEnabled: true, metadataRefreshMode: "FullRefresh", replaceAllMetadata: "true");

            _ = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);
            await harness.TriggerItemUpdatedAsync(ItemUpdateType.MetadataDownload).ConfigureAwait(false);

            Assert.IsNull(harness.CandidateStore.Peek(harness.Episode.Id), "ReplaceAllMetadata=true 时不应入队 candidate。");
            Assert.AreEqual(0, harness.Persistence.SaveCallCount, "没有 candidate 时不应触发持久化。");
            Assert.AreEqual("第 1 集", harness.Episode.Name, "真实宿主 Episode 标题应保持默认值。");
        }

        [TestMethod]
        public async Task SearchMissingMetadataFlow_MetadataEditOnly_DoesNotApplyBackfill()
        {
            using var harness = CreateHarness(featureEnabled: true, metadataRefreshMode: "FullRefresh", replaceAllMetadata: "false");

            _ = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);
            var queuedCandidateBeforeEdit = harness.CandidateStore.Peek(harness.Episode.Id);

            await harness.TriggerItemUpdatedAsync(ItemUpdateType.MetadataEdit).ConfigureAwait(false);

            Assert.IsNotNull(queuedCandidateBeforeEdit, "成功入队是验证 MetadataEdit-only no-op 的前提。");
            Assert.IsNotNull(harness.CandidateStore.Peek(harness.Episode.Id), "MetadataEdit-only 不应消耗 candidate。");
            Assert.AreEqual(0, harness.Persistence.SaveCallCount, "MetadataEdit-only 不应触发持久化。");
            Assert.AreEqual("第 1 集", harness.Episode.Name, "MetadataEdit-only 不应修改宿主 Episode 标题。");
        }

        [TestMethod]
        public async Task SearchMissingMetadataFlow_FeatureDisabled_DoesNotQueueOrApplyBackfill()
        {
            using var harness = CreateHarness(featureEnabled: false, metadataRefreshMode: "FullRefresh", replaceAllMetadata: "false");

            _ = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);
            await harness.TriggerItemUpdatedAsync(ItemUpdateType.MetadataDownload).ConfigureAwait(false);

            Assert.IsNull(harness.CandidateStore.Peek(harness.Episode.Id), "feature 关闭时 provider 不应入队 candidate。");
            Assert.AreEqual(0, harness.Persistence.SaveCallCount, "feature 关闭时 postprocess 不应触发持久化。");
            Assert.AreEqual("第 1 集", harness.Episode.Name, "feature 关闭时宿主 Episode 标题应保持默认值。");
        }

        [TestMethod]
        public async Task SearchMissingMetadataFlow_NullLookupLanguageWithEpisodePreferredZhCn_PersistsAndBackfillsEpisodeTitle()
        {
            using var harness = CreateHarness(
                featureEnabled: true,
                metadataRefreshMode: "FullRefresh",
                replaceAllMetadata: "false",
                metadataLanguage: null,
                episodePreferredMetadataLanguage: "zh-CN",
                tmdbLanguage: "zh-CN",
                tmdbImageLanguages: "zh-CN");

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);
            var queuedCandidate = harness.CandidateStore.Peek(harness.Episode.Id);

            await harness.TriggerItemUpdatedAsync(ItemUpdateType.MetadataDownload).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual("皇后回宫", result.Item!.Name);
            Assert.IsNotNull(queuedCandidate, "当 lookup language 缺失但当前 Episode 偏好 zh-CN 时，provider 仍应入队 candidate。");
            Assert.AreEqual(1, harness.Persistence.SaveCallCount, "MetadataDownload 事件仍应驱动持久化。");
            Assert.AreEqual("皇后回宫", harness.Episode.Name, "当前 Episode 偏好 zh-CN 时，来源语言合同仍应成功应用标题回填。");
        }

        [TestMethod]
        public async Task SearchMissingMetadataFlow_GenericDetailsTitleWithZhCnTranslation_PersistsAndBackfillsEpisodeTitle()
        {
            using var harness = CreateHarness(
                featureEnabled: true,
                metadataRefreshMode: "FullRefresh",
                replaceAllMetadata: "false",
                metadataLanguage: "zh-CN",
                episodePreferredMetadataLanguage: null,
                tmdbLanguage: "zh-CN",
                tmdbImageLanguages: "zh-CN",
                tmdbEpisodeName: "第 1 集",
                tmdbZhCnTranslationTitle: "皇后回宫");

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);
            var queuedCandidate = harness.CandidateStore.Peek(harness.Episode.Id);

            await harness.TriggerItemUpdatedAsync(ItemUpdateType.MetadataDownload).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual("皇后回宫", result.Item!.Name);
            Assert.IsNotNull(queuedCandidate, "generic details title 有效 zh-CN translation 时，provider 仍应入队 candidate。");
            Assert.AreEqual("皇后回宫", queuedCandidate!.CandidateTitle);
            Assert.AreEqual(1, harness.Persistence.SaveCallCount, "translation title 被接受后，应继续沿用现有 MetadataDownload 持久化链路。");
            Assert.AreEqual("皇后回宫", harness.Episode.Name, "最终宿主 Episode 标题应被回填为 zh-CN translation title。");
        }

        [TestMethod]
        public async Task SearchMissingMetadataFlow_BareZhLookupLanguage_PromotesToZhCnAndBackfillsEpisodeTitle()
        {
            using var harness = CreateHarness(
                featureEnabled: true,
                metadataRefreshMode: "FullRefresh",
                replaceAllMetadata: "false",
                metadataLanguage: "zh",
                episodePreferredMetadataLanguage: null,
                tmdbLanguage: "zh-CN",
                tmdbImageLanguages: "zh-CN",
                tmdbEpisodeName: "皇后回宫");

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);
            var queuedCandidate = harness.CandidateStore.Peek(harness.Episode.Id);

            await harness.TriggerItemUpdatedAsync(ItemUpdateType.MetadataDownload).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual("皇后回宫", result.Item!.Name);
            Assert.IsNotNull(queuedCandidate, "当 lookup language 为 bare zh 时，应先提升到 zh-CN 目标来源，再进入当前 MetadataDownload 回填链路。");
            Assert.AreEqual(1, harness.Persistence.SaveCallCount, "bare zh 提升为 zh-CN 后，MetadataDownload 事件仍应驱动持久化。");
            Assert.AreEqual("皇后回宫", harness.Episode.Name, "bare zh 提升为 zh-CN 后，宿主 Episode 标题应被正常回填。");
        }

        private FlowHarness CreateHarness(
            bool featureEnabled,
            string metadataRefreshMode,
            string replaceAllMetadata,
            string? metadataLanguage = "zh-CN",
            string? episodePreferredMetadataLanguage = null,
            string tmdbLanguage = "zh-CN",
            string tmdbImageLanguages = "zh-CN",
            string tmdbEpisodeName = "  皇后回宫  ",
            string? tmdbZhCnTranslationTitle = null)
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = featureEnabled;
            return new FlowHarness(this.loggerFactory, metadataRefreshMode, replaceAllMetadata, metadataLanguage, episodePreferredMetadataLanguage, tmdbLanguage, tmdbImageLanguages, tmdbEpisodeName, tmdbZhCnTranslationTitle);
        }

        private static EpisodeProvider CreateProvider(
            ILibraryManager libraryManager,
            IHttpContextAccessor httpContextAccessor,
            TmdbApi tmdbApi,
            IEpisodeTitleBackfillCandidateStore store,
            ILoggerFactory loggerFactory)
        {
            var constructor = typeof(EpisodeProvider).GetConstructor(new[]
            {
                typeof(IHttpClientFactory),
                typeof(ILoggerFactory),
                typeof(ILibraryManager),
                typeof(IHttpContextAccessor),
                typeof(DoubanApi),
                typeof(TmdbApi),
                typeof(OmdbApi),
                typeof(ImdbApi),
                typeof(TvdbApi),
                typeof(IEpisodeTitleBackfillCandidateStore),
            });

            Assert.IsNotNull(constructor, "EpisodeProvider 尚未注入 IEpisodeTitleBackfillCandidateStore");

            return (EpisodeProvider)constructor!.Invoke(new object[]
            {
                new DefaultHttpClientFactory(),
                loggerFactory,
                libraryManager,
                httpContextAccessor,
                new DoubanApi(loggerFactory),
                tmdbApi,
                new OmdbApi(loggerFactory),
                new ImdbApi(loggerFactory),
                new TvdbApi(loggerFactory),
                store,
            });
        }

        private static DefaultHttpContext CreateHttpContext(string metadataRefreshMode, string replaceAllMetadata)
        {
            var context = new DefaultHttpContext();
            context.Request.QueryString = new QueryString($"?metadataRefreshMode={metadataRefreshMode}&replaceAllMetadata={replaceAllMetadata}");
            return context;
        }

        private static EpisodeInfo CreateEpisodeInfo(string? metadataLanguage = "zh-CN")
        {
            return new EpisodeInfo
            {
                Name = "第 1 集",
                Path = "/library/tv/series-a/Season 01/episode-01.mkv",
                MetadataLanguage = metadataLanguage,
                ParentIndexNumber = 1,
                IndexNumber = 1,
                SeriesDisplayOrder = string.Empty,
                SeriesProviderIds = new Dictionary<string, string>
                {
                    [MetadataProvider.Tmdb.ToString()] = "123",
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

        private static void SeedEpisodeTranslationTitle(TmdbApi tmdbApi, int seriesTmdbId, int seasonNumber, int episodeNumber, string language, string? title)
        {
            var cacheField = typeof(TmdbApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(cacheField, "TmdbApi.memoryCache 未找到");

            var cache = cacheField!.GetValue(tmdbApi) as MemoryCache;
            Assert.IsNotNull(cache, "TmdbApi.memoryCache 不是有效的 MemoryCache");

            var key = $"episode-translation-title-{seriesTmdbId}-s{seasonNumber}e{episodeNumber}-{language}";
            cache!.Set(
                key,
                title == null
                    ? null
                    : new EpisodeLocalizedValue
                    {
                        Value = title,
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
            private readonly EpisodeTitleBackfillItemUpdatedWorker worker;
            private bool workerStarted;

            public FlowHarness(
                ILoggerFactory loggerFactory,
                string metadataRefreshMode,
                string replaceAllMetadata,
                string? metadataLanguage,
                string? episodePreferredMetadataLanguage,
                string tmdbLanguage,
                string tmdbImageLanguages,
                string tmdbEpisodeName,
                string? tmdbZhCnTranslationTitle)
            {
                this.CandidateStore = new InMemoryEpisodeTitleBackfillCandidateStore();
                this.Persistence = new RecordingEpisodeTitleBackfillPersistence();
                this.Info = CreateEpisodeInfo(metadataLanguage);
                this.Episode = new Episode
                {
                    Id = Guid.NewGuid(),
                    Name = this.Info.Name,
                    Path = this.Info.Path,
                    PreferredMetadataLanguage = episodePreferredMetadataLanguage,
                };

                this.LibraryManagerStub = new Mock<ILibraryManager>();
                this.LibraryManagerStub
                    .Setup(x => x.FindByPath(this.Info.Path, false))
                    .Returns(this.Episode);

                this.HttpContextAccessor = new HttpContextAccessor
                {
                    HttpContext = CreateHttpContext(metadataRefreshMode, replaceAllMetadata),
                };

                var tmdbApi = new TmdbApi(loggerFactory);
                SeedEpisode(tmdbApi, 123, 1, 1, tmdbLanguage, tmdbImageLanguages, new TvEpisode
                {
                    Name = tmdbEpisodeName,
                });
                SeedEpisodeTranslationTitle(tmdbApi, 123, 1, 1, "zh-CN", tmdbZhCnTranslationTitle);

                this.Provider = CreateProvider(this.LibraryManagerStub.Object, this.HttpContextAccessor, tmdbApi, this.CandidateStore, loggerFactory);

                var postProcessService = new EpisodeTitleBackfillPostProcessService(
                    this.CandidateStore,
                    this.Persistence,
                    loggerFactory.CreateLogger<EpisodeTitleBackfillPostProcessService>());
                this.worker = new EpisodeTitleBackfillItemUpdatedWorker(
                    this.LibraryManagerStub.Object,
                    postProcessService,
                    loggerFactory.CreateLogger<EpisodeTitleBackfillItemUpdatedWorker>());
            }

            public InMemoryEpisodeTitleBackfillCandidateStore CandidateStore { get; }

            public RecordingEpisodeTitleBackfillPersistence Persistence { get; }

            public EpisodeInfo Info { get; }

            public Episode Episode { get; }

            public IHttpContextAccessor HttpContextAccessor { get; }

            public Mock<ILibraryManager> LibraryManagerStub { get; }

            public EpisodeProvider Provider { get; }

            public async Task TriggerItemUpdatedAsync(ItemUpdateType updateReason)
            {
                if (!this.workerStarted)
                {
                    await this.worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
                    this.workerStarted = true;
                }

                this.LibraryManagerStub.Raise(
                    x => x.ItemUpdated += null,
                    this.LibraryManagerStub.Object,
                    new ItemChangeEventArgs
                    {
                        Item = this.Episode,
                        UpdateReason = updateReason,
                    });
            }

            public void Dispose()
            {
                if (this.workerStarted)
                {
                    this.worker.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
                }

                this.Provider.Dispose();
            }
        }

        private sealed class RecordingEpisodeTitleBackfillPersistence : IEpisodeTitleBackfillPersistence
        {
            public List<Episode> SavedEpisodes { get; } = new List<Episode>();

            public int SaveCallCount => this.SavedEpisodes.Count;

            public Task SaveAsync(Episode episode, CancellationToken cancellationToken)
            {
                this.SavedEpisodes.Add(episode);
                return Task.CompletedTask;
            }
        }
    }
}
