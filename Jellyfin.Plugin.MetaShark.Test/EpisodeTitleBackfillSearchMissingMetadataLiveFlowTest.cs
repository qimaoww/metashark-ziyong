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
    public class EpisodeTitleBackfillSearchMissingMetadataLiveFlowTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-episode-title-backfill-live-flow-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");

        private readonly ILoggerFactory silentLoggerFactory = LoggerFactory.Create(builder => { });

        [TestCleanup]
        public void Cleanup()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = false;
        }

        [TestMethod]
        public async Task SearchMissingMetadataFlow_WhenItemUpdatedNeverArrives_UsesDeferredRetryAndBackfillsToday()
        {
            using var harness = this.CreateHarness(featureEnabled: true);

            var result = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);
            var queuedCandidate = harness.CandidateStore.Peek(harness.Episode.Id);

            Assert.IsNotNull(result.Item, "provider 必须先解析出 candidate title，这样 deferred retry 的绿灯才真正说明补偿路径打通。");
            Assert.AreEqual("皇后回宫", result.Item!.Name, "provider 侧应先拿到候选标题，避免把成功误判成上游取数变化。");
            Assert.IsNotNull(queuedCandidate, "provider 必须先入队 candidate，deferred retry 才有可补偿对象。");

            await harness.ExecuteDeferredRetryDueCycleAsync(queuedCandidate!.NextAttemptAtUtc.AddSeconds(1)).ConfigureAwait(false);

            Assert.AreEqual(1, harness.Persistence.SaveCallCount, "即使 live refresh 没有稳定送达 ItemUpdated，deferred retry 也应在同进程窗口内完成一次持久化补偿。");
            Assert.AreSame(harness.Episode, harness.Persistence.SavedEpisodes.Single(), "deferred retry 成功时，应持久化当前宿主 Episode 实例。");
            Assert.IsNull(harness.CandidateStore.Peek(harness.Episode.Id), "deferred retry 成功后不应继续残留 itemId work item。");
            Assert.IsNull(harness.CandidateStore.PeekByPath(harness.Info.Path), "deferred retry 成功后路径索引也应清空，避免同一路径 work item 卡死。");
            Assert.AreEqual(
                "皇后回宫",
                harness.Episode.Name,
                "task 3 后即使 live refresh 没有稳定送达 ItemUpdated，也应依靠 deferred retry 在同进程窗口内完成标题回填。");
        }

        [TestMethod]
        public async Task SearchMissingMetadataFlow_WhenItemIdChangesAcrossRefresh_DoesNotBackfillToday_RegressionNowRebindsAndBackfillsToday()
        {
            using var harness = this.CreateHarness(featureEnabled: true);

            _ = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);
            var queuedCandidate = harness.CandidateStore.Peek(harness.Episode.Id);
            var recreatedEpisode = harness.CreateReplacementEpisode();

            Assert.IsNotNull(queuedCandidate, "provider 必须先把旧 itemId 的 candidate 入队，这个红灯才说明是 itemId 重建导致关联失效。");
            Assert.AreNotEqual(queuedCandidate!.ItemId, recreatedEpisode.Id, "测试前提要求 refresh 后宿主 Episode 已换成新的 itemId。");

            await harness.TriggerItemUpdatedAsync(recreatedEpisode, ItemUpdateType.MetadataDownload).ConfigureAwait(false);

            Assert.AreEqual(1, harness.Persistence.SaveCallCount, "refresh 重建 itemId 后，live 链路仍应完成一次持久化，证明 path-aware recovery 已把 candidate 绑定到新的 Episode 实例。");
            Assert.AreSame(recreatedEpisode, harness.Persistence.SavedEpisodes.Single(), "成功持久化的应是 refresh 后的新 Episode 实例，而不是旧 itemId 对应的宿主对象。");
            Assert.IsNull(harness.CandidateStore.Peek(queuedCandidate.ItemId), "旧 itemId 的 candidate 不应继续残留；task 2 后应通过 path-aware recovery/rebind 把它从旧键位解绑。");
            Assert.IsNull(harness.CandidateStore.Peek(recreatedEpisode.Id), "成功重绑并应用后，也不应在新 itemId 下遗留 candidate。");
            Assert.IsNull(harness.CandidateStore.PeekByPath(harness.Info.Path), "成功应用后路径索引也应被清空，避免同一路径 work item 卡死。");
            Assert.AreEqual(
                "皇后回宫",
                recreatedEpisode.Name,
                "task 2 后 refresh 即使重建了 itemId，也应依靠 path-aware recovery/rebind 在新 Episode 上完成标题回填。");
        }

        [TestMethod]
        public async Task SearchMissingMetadataFlow_WhenProviderDecisionIsInvisibleInInfoLogs_IsNowVisibleAtInformation()
        {
            using var loggerProvider = new TestLoggerProvider();
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(loggerProvider));
            using var harness = this.CreateHarness(featureEnabled: true, loggerFactory: loggerFactory);

            _ = await harness.Provider.GetMetadata(harness.Info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(harness.CandidateStore.Peek(harness.Episode.Id), "provider 必须先成功入队 candidate，这个红灯才是在表达 live 日志可观测性不足。");
            Assert.IsTrue(
                loggerProvider.Messages.Any(message => message.LogLevel == LogLevel.Information
                    && message.Category.Contains(nameof(EpisodeProvider), StringComparison.Ordinal)
                    && message.Message.Contains("EpisodeTitleBackfillDecision", StringComparison.Ordinal)
                    && message.Message.Contains("CandidateQueued", StringComparison.Ordinal)
                    && message.Message.Contains($"itemId={harness.Episode.Id}", StringComparison.Ordinal)
                    && message.Message.Contains($"itemPath={harness.Info.Path}", StringComparison.Ordinal)
                    && message.Message.Contains("metadataRefreshMode=FullRefresh", StringComparison.Ordinal)
                    && message.Message.Contains("replaceAllMetadata=false", StringComparison.Ordinal)),
                "task 3 后 provider 的 decision 观察点必须提升到 Information，并带上 itemId/itemPath，live 排障时才能直接看到为什么入队。");
            Assert.IsTrue(
                loggerProvider.Messages.Any(message => message.LogLevel == LogLevel.Information
                    && message.Category.Contains(nameof(EpisodeProvider), StringComparison.Ordinal)
                    && message.Message.Contains("EpisodeTitleBackfillQueued", StringComparison.Ordinal)
                    && message.Message.Contains($"itemId={harness.Episode.Id}", StringComparison.Ordinal)
                    && message.Message.Contains($"itemPath={harness.Info.Path}", StringComparison.Ordinal)
                    && message.Message.Contains("metadataRefreshMode=FullRefresh", StringComparison.Ordinal)
                    && message.Message.Contains("replaceAllMetadata=false", StringComparison.Ordinal)),
                "task 3 后 provider 的 queue 观察点也必须在 Information 可见，避免 live 只保留 Info 时丢失排障证据。");
        }

        private FlowHarness CreateHarness(bool featureEnabled, ILoggerFactory? loggerFactory = null, string metadataRefreshMode = "FullRefresh", string replaceAllMetadata = "false")
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = featureEnabled;
            return new FlowHarness(loggerFactory ?? this.silentLoggerFactory, metadataRefreshMode, replaceAllMetadata);
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

        private static EpisodeInfo CreateEpisodeInfo()
        {
            return new EpisodeInfo
            {
                Name = "第 1 集",
                Path = "/library/tv/series-a/Season 01/episode-01.mkv",
                MetadataLanguage = "zh-CN",
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
            private readonly EpisodeTitleBackfillDeferredRetryWorker deferredRetryWorker;
            private readonly EpisodeTitleBackfillItemUpdatedWorker worker;
            private bool workerStarted;

            public FlowHarness(ILoggerFactory loggerFactory, string metadataRefreshMode, string replaceAllMetadata)
            {
                this.CandidateStore = new InMemoryEpisodeTitleBackfillCandidateStore();
                this.Persistence = new RecordingEpisodeTitleBackfillPersistence();
                this.Info = CreateEpisodeInfo();
                this.Episode = new Episode
                {
                    Id = Guid.NewGuid(),
                    Name = this.Info.Name,
                    Path = this.Info.Path,
                };

                this.LibraryManagerStub = new Mock<ILibraryManager>();
                this.LibraryManagerStub
                    .Setup(x => x.FindByPath(this.Info.Path, false))
                    .Returns(() => this.Episode);

                this.HttpContextAccessor = new HttpContextAccessor
                {
                    HttpContext = CreateHttpContext(metadataRefreshMode, replaceAllMetadata),
                };

                var tmdbApi = new TmdbApi(loggerFactory);
                SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode
                {
                    Name = "  皇后回宫  ",
                });

                this.Provider = CreateProvider(this.LibraryManagerStub.Object, this.HttpContextAccessor, tmdbApi, this.CandidateStore, loggerFactory);

                var pendingResolver = new EpisodeTitleBackfillPendingResolver(this.CandidateStore, this.LibraryManagerStub.Object);
                var postProcessService = new EpisodeTitleBackfillPostProcessService(
                    this.CandidateStore,
                    pendingResolver,
                    this.Persistence,
                    loggerFactory.CreateLogger<EpisodeTitleBackfillPostProcessService>());
                this.worker = new EpisodeTitleBackfillItemUpdatedWorker(
                    this.LibraryManagerStub.Object,
                    postProcessService,
                    loggerFactory.CreateLogger<EpisodeTitleBackfillItemUpdatedWorker>());
                this.deferredRetryWorker = new EpisodeTitleBackfillDeferredRetryWorker(
                    this.CandidateStore,
                    pendingResolver,
                    postProcessService,
                    loggerFactory.CreateLogger<EpisodeTitleBackfillDeferredRetryWorker>());
            }

            public InMemoryEpisodeTitleBackfillCandidateStore CandidateStore { get; }

            public RecordingEpisodeTitleBackfillPersistence Persistence { get; }

            public EpisodeInfo Info { get; }

            public Episode Episode { get; }

            public IHttpContextAccessor HttpContextAccessor { get; }

            public Mock<ILibraryManager> LibraryManagerStub { get; }

            public EpisodeProvider Provider { get; }

            public async Task ExecuteDeferredRetryDueCycleAsync(DateTimeOffset nowUtc)
            {
                var executeMethod = typeof(EpisodeTitleBackfillDeferredRetryWorker).GetMethod(
                    "ExecuteDueCycleAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(DateTimeOffset), typeof(CancellationToken) },
                    null);
                Assert.IsNotNull(executeMethod, "Expected ExecuteDueCycleAsync(DateTimeOffset, CancellationToken) helper for deterministic deferred retry verification.");

                var task = executeMethod!.Invoke(this.deferredRetryWorker, new object[] { nowUtc, CancellationToken.None }) as Task;
                Assert.IsNotNull(task, "ExecuteDeferredRetryDueCycleAsync should return Task.");
                await task!.ConfigureAwait(false);
            }

            public Episode CreateReplacementEpisode()
            {
                return new Episode
                {
                    Id = Guid.NewGuid(),
                    Name = "第 1 集",
                    Path = this.Info.Path,
                };
            }

            public async Task TriggerItemUpdatedAsync(Episode episode, ItemUpdateType updateReason)
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
                        Item = episode,
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
                this.deferredRetryWorker.Dispose();
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

        private sealed class TestLoggerProvider : ILoggerProvider
        {
            private readonly List<LoggedMessage> messages = new List<LoggedMessage>();

            public IReadOnlyList<LoggedMessage> Messages => this.messages;

            public ILogger CreateLogger(string categoryName)
            {
                return new TestLogger(categoryName, this.messages);
            }

            public void Dispose()
            {
            }
        }

        private sealed class TestLogger : ILogger
        {
            private readonly string categoryName;
            private readonly List<LoggedMessage> messages;

            public TestLogger(string categoryName, List<LoggedMessage> messages)
            {
                this.categoryName = categoryName;
                this.messages = messages;
            }

            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull
            {
                return NullScope.Instance;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                this.messages.Add(new LoggedMessage(this.categoryName, logLevel, eventId, formatter(state, exception)));
            }
        }

        private sealed record LoggedMessage(string Category, LogLevel LogLevel, EventId EventId, string Message);

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new NullScope();

            public void Dispose()
            {
            }
        }
    }
}
