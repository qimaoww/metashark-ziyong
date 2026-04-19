using Jellyfin.Plugin.MetaShark;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Test.Logging;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Common.Net;
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
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class EpisodeTitleBackfillDeferredRetryWorkerTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-episode-title-backfill-deferred-worker-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");

        [TestCleanup]
        public void Cleanup()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = false;
        }

        [TestMethod]
        public async Task ExecuteDueCycleAsync_WhenPathFallbackFindsRecreatedEpisode_RebindsAndApplies()
        {
            SetFeatureEnabled(true);

            var itemPath = "/library/tv/series-a/Season 01/episode-01.mkv";
            var originalItemId = Guid.NewGuid();
            var recreatedEpisode = new Episode
            {
                Id = Guid.NewGuid(),
                Name = "第 1 集",
                Path = itemPath,
            };

            var candidateStore = new InMemoryEpisodeTitleBackfillCandidateStore();
            candidateStore.Save(CreatePathAwareCandidate(originalItemId, itemPath, DateTimeOffset.UtcNow.AddSeconds(-30), DateTimeOffset.UtcNow.AddSeconds(-1), 0));

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(itemPath, false))
                .Returns(recreatedEpisode);

            var persistence = new RecordingEpisodeTitleBackfillPersistence();
            var postProcessService = CreatePostProcessService(candidateStore, persistence);
            var worker = CreateWorker(candidateStore, libraryManagerStub.Object, postProcessService);

            await ExecuteDueCycleAsync(worker, DateTimeOffset.UtcNow).ConfigureAwait(false);

            Assert.AreEqual("皇后回宫", recreatedEpisode.Name);
            Assert.AreEqual(1, persistence.SaveCallCount, "deferred retry 应命中同一个 post-process apply 路径。");
            Assert.IsNull(candidateStore.Peek(recreatedEpisode.Id), "成功应用后应清除当前 itemId 对应的 candidate。");
            Assert.IsNull(PeekCandidateByPath(candidateStore, itemPath), "成功应用后路径索引也应被清空。");
        }

        [TestMethod]
        public async Task ExecuteDueCycleAsync_WhenCandidateExpired_RemovesPendingWork()
        {
            SetFeatureEnabled(true);

            var itemId = Guid.NewGuid();
            var itemPath = "/library/tv/series-a/Season 01/episode-01.mkv";
            var nowUtc = DateTimeOffset.UtcNow;
            var candidateStore = new InMemoryEpisodeTitleBackfillCandidateStore();
            candidateStore.Save(CreatePathAwareCandidate(itemId, itemPath, nowUtc.AddMinutes(-5), nowUtc.AddMinutes(-1), 2, nowUtc.AddSeconds(-1)));

            var libraryManagerStub = new Mock<ILibraryManager>();
            var persistence = new RecordingEpisodeTitleBackfillPersistence();
            var postProcessService = CreatePostProcessService(candidateStore, persistence);
            var worker = CreateWorker(candidateStore, libraryManagerStub.Object, postProcessService);

            await ExecuteDueCycleAsync(worker, nowUtc).ConfigureAwait(false);

            Assert.IsNull(candidateStore.Peek(itemId), "超过 TTL 的 deferred candidate 应从 itemId 索引中删除。");
            Assert.IsNull(PeekCandidateByPath(candidateStore, itemPath), "超过 TTL 的 deferred candidate 应从路径索引中删除。");
            Assert.AreEqual(0, persistence.SaveCallCount, "过期 candidate 不应继续触发 apply。");
        }

        [TestMethod]
        public async Task ExecuteDueCycleAsync_WhenMoreThanTwentyItemsAreDue_ProcessesAtMostTwenty()
        {
            SetFeatureEnabled(true);

            var nowUtc = DateTimeOffset.UtcNow;
            var candidateStore = new InMemoryEpisodeTitleBackfillCandidateStore();
            var episodesByPath = new Dictionary<string, Episode>(StringComparer.Ordinal);

            for (var index = 0; index < 25; index++)
            {
                var episodeId = Guid.NewGuid();
                var itemPath = $"/library/tv/series-a/Season 01/episode-{index:D2}.mkv";
                episodesByPath[itemPath] = new Episode
                {
                    Id = episodeId,
                    Name = "第 1 集",
                    Path = itemPath,
                };

                candidateStore.Save(CreatePathAwareCandidate(episodeId, itemPath, nowUtc.AddSeconds(-30), nowUtc.AddSeconds(-1), 0, nowUtc.AddMinutes(2)));
            }

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(It.IsAny<string>(), false))
                .Returns((string path, bool _) => episodesByPath[path]);

            var persistence = new RecordingEpisodeTitleBackfillPersistence();
            var postProcessService = CreatePostProcessService(candidateStore, persistence);
            var worker = CreateWorker(candidateStore, libraryManagerStub.Object, postProcessService);

            await ExecuteDueCycleAsync(worker, nowUtc).ConfigureAwait(false);

            Assert.AreEqual(20, persistence.SaveCallCount, "每个 cycle 最多只允许处理 20 条 due candidate。");
            var remainingCount = episodesByPath.Keys.Count(path => PeekCandidateByPath(candidateStore, path) != null);
            Assert.AreEqual(5, remainingCount, "超过上限的 due candidate 应留给后续 cycle 继续处理。");
        }

        [TestMethod]
        public async Task ExecuteDueCycleAsync_WhenPostProcessThrows_LogsUnifiedRetryFailure()
        {
            SetFeatureEnabled(true);

            var nowUtc = DateTimeOffset.UtcNow;
            var itemId = Guid.NewGuid();
            var itemPath = "/library/tv/series-a/Season 01/episode-error.mkv";
            var candidateStore = new InMemoryEpisodeTitleBackfillCandidateStore();
            candidateStore.Save(CreatePathAwareCandidate(itemId, itemPath, nowUtc.AddMinutes(-1), nowUtc.AddSeconds(-1), 0));

            var episode = new Episode
            {
                Id = itemId,
                Name = "第 1 集",
                Path = itemPath,
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub.Setup(x => x.FindByPath(itemPath, false)).Returns(episode);

            var postProcessServiceStub = new Mock<IEpisodeTitleBackfillPostProcessService>();
            postProcessServiceStub
                .Setup(x => x.TryApplyAsync(It.IsAny<ItemChangeEventArgs>(), IEpisodeTitleBackfillPostProcessService.DeferredRetryTrigger, CancellationToken.None))
                .ThrowsAsync(new InvalidOperationException("boom"));

            var loggerStub = new Mock<ILogger<EpisodeTitleBackfillDeferredRetryWorker>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            var worker = new EpisodeTitleBackfillDeferredRetryWorker(
                candidateStore,
                CreateResolver(candidateStore, libraryManagerStub.Object),
                postProcessServiceStub.Object,
                loggerStub.Object);

            await ExecuteDueCycleAsync(worker, nowUtc).ConfigureAwait(false);

            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Error,
                expectException: true,
                stateContains: new Dictionary<string, object?>
                {
                    ["ItemId"] = itemId,
                    ["ItemPath"] = itemPath,
                },
                originalFormatContains: "[MetaShark] 剧集标题回填延迟重试失败",
                messageContains: ["[MetaShark] 剧集标题回填延迟重试失败", "trigger=DeferredRetry", $"itemId={itemId}", $"itemPath={itemPath}"]);
        }

        [TestMethod]
        public async Task ExecuteAsync_WhenItemUpdatedNeverArrives_UsesDeferredRetryAndLogsAnchors()
        {
            SetFeatureEnabled(true);

            using var loggerProvider = new TestLoggerProvider();
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(loggerProvider));
            var candidateStore = new InMemoryEpisodeTitleBackfillCandidateStore();
            var info = CreateEpisodeInfo();
            var episode = new Episode
            {
                Id = Guid.NewGuid(),
                Name = info.Name,
                Path = info.Path,
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(episode);

            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = CreateHttpContext("FullRefresh", "false"),
            };

            var tmdbApi = new TmdbApi(loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode
            {
                Name = "  皇后回宫  ",
            });

            using var provider = CreateProvider(libraryManagerStub.Object, httpContextAccessor, tmdbApi, candidateStore, loggerFactory);
            var providerResult = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);
            var queuedCandidate = candidateStore.Peek(episode.Id);

            Assert.IsNotNull(providerResult.Item, "provider 必须先产出 candidate title，后续 deferred retry 才真正覆盖 live 缺失 ItemUpdated 的窗口。");
            Assert.IsNotNull(queuedCandidate, "provider 必须先把 work item 入队，deferred retry 才有可补偿对象。");

            var persistence = new RecordingEpisodeTitleBackfillPersistence();
            var resolver = CreateResolver(candidateStore, libraryManagerStub.Object);
            var postProcessLoggerStub = new Mock<ILogger<EpisodeTitleBackfillPostProcessService>>();
            postProcessLoggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            var postProcessService = new EpisodeTitleBackfillPostProcessService(candidateStore, resolver, persistence, postProcessLoggerStub.Object);
            var worker = CreateWorker(candidateStore, resolver, postProcessService, loggerFactory);

            await ExecuteDueCycleAsync(worker, queuedCandidate!.NextAttemptAtUtc.AddSeconds(1)).ConfigureAwait(false);

            Assert.AreEqual("皇后回宫", episode.Name);
            Assert.AreEqual(1, persistence.SaveCallCount, "缺少 ItemUpdated 时应由 deferred retry 命中同一条 apply 路径完成回填。");
            Assert.IsNull(candidateStore.Peek(episode.Id), "成功补偿后不应遗留 itemId 索引。");
            Assert.IsNull(PeekCandidateByPath(candidateStore, info.Path), "成功补偿后路径索引也应清空。");
            AssertLoggedMessage(
                loggerProvider,
                LogLevel.Information,
                nameof(EpisodeProvider),
                "[MetaShark] 剧集标题回填决策",
                "CandidateQueued",
                $"itemId={episode.Id}",
                $"itemPath={info.Path}");
            AssertLoggedMessage(
                loggerProvider,
                LogLevel.Information,
                nameof(EpisodeProvider),
                "[MetaShark] 已排队剧集标题回填",
                $"itemId={episode.Id}",
                $"itemPath={info.Path}");
            LogAssert.AssertLoggedOnce(
                postProcessLoggerStub,
                LogLevel.Information,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["ItemId"] = episode.Id,
                    ["Trigger"] = IEpisodeTitleBackfillPostProcessService.DeferredRetryTrigger,
                    ["ItemPath"] = info.Path,
                    ["CandidateTitle"] = "皇后回宫",
                    ["UpdateReason"] = ItemUpdateType.MetadataDownload,
                },
                originalFormatContains: "[MetaShark] 已应用剧集标题回填",
                messageContains: ["[MetaShark] 已应用剧集标题回填", "trigger=DeferredRetry", $"itemPath={info.Path}"]);
        }

        private static EpisodeTitleBackfillPostProcessService CreatePostProcessService(
            IEpisodeTitleBackfillCandidateStore candidateStore,
            IEpisodeTitleBackfillPersistence persistence)
        {
            var loggerStub = new Mock<ILogger<EpisodeTitleBackfillPostProcessService>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
            return new EpisodeTitleBackfillPostProcessService(candidateStore, persistence, loggerStub.Object);
        }

        private static EpisodeTitleBackfillPostProcessService CreatePostProcessService(
            IEpisodeTitleBackfillCandidateStore candidateStore,
            ILibraryManager libraryManager,
            IEpisodeTitleBackfillPersistence persistence)
        {
            return CreatePostProcessService(
                candidateStore,
                CreateResolver(candidateStore, libraryManager),
                persistence,
                LoggerFactory.Create(builder => { }));
        }

        private static EpisodeTitleBackfillPostProcessService CreatePostProcessService(
            IEpisodeTitleBackfillCandidateStore candidateStore,
            IEpisodeTitleBackfillPendingResolver resolver,
            IEpisodeTitleBackfillPersistence persistence,
            ILoggerFactory loggerFactory)
        {
            return new EpisodeTitleBackfillPostProcessService(
                candidateStore,
                resolver,
                persistence,
                loggerFactory.CreateLogger<EpisodeTitleBackfillPostProcessService>());
        }

        private static EpisodeTitleBackfillDeferredRetryWorker CreateWorker(
            IEpisodeTitleBackfillCandidateStore candidateStore,
            IEpisodeTitleBackfillPendingResolver resolver,
            IEpisodeTitleBackfillPostProcessService postProcessService,
            ILoggerFactory? loggerFactory = null)
        {
            loggerFactory ??= LoggerFactory.Create(builder => { });
            return new EpisodeTitleBackfillDeferredRetryWorker(
                candidateStore,
                resolver,
                postProcessService,
                loggerFactory.CreateLogger<EpisodeTitleBackfillDeferredRetryWorker>());
        }

        private static EpisodeTitleBackfillDeferredRetryWorker CreateWorker(
            IEpisodeTitleBackfillCandidateStore candidateStore,
            ILibraryManager libraryManager,
            IEpisodeTitleBackfillPostProcessService postProcessService)
        {
            return CreateWorker(candidateStore, CreateResolver(candidateStore, libraryManager), postProcessService);
        }

        private static IEpisodeTitleBackfillPendingResolver CreateResolver(IEpisodeTitleBackfillCandidateStore candidateStore, ILibraryManager libraryManager)
        {
            return new EpisodeTitleBackfillPendingResolver(candidateStore, libraryManager);
        }

        private static async Task ExecuteDueCycleAsync(EpisodeTitleBackfillDeferredRetryWorker worker, DateTimeOffset nowUtc)
        {
            var executeMethod = worker.GetType().GetMethod(
                "ExecuteDueCycleAsync",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(DateTimeOffset), typeof(CancellationToken) },
                null);
            Assert.IsNotNull(executeMethod, "Expected ExecuteDueCycleAsync(DateTimeOffset, CancellationToken) helper for deterministic worker verification.");

            var task = executeMethod!.Invoke(worker, new object[] { nowUtc, CancellationToken.None }) as Task;
            Assert.IsNotNull(task, "ExecuteDueCycleAsync should return Task.");
            await task!.ConfigureAwait(false);
        }

        private static Type GetWorkerType()
        {
            var workerType = typeof(EpisodeTitleBackfillItemUpdatedWorker).Assembly.GetType("Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfillDeferredRetryWorker");
            Assert.IsNotNull(workerType, "Expected EpisodeTitleBackfillDeferredRetryWorker type for deferred compensation.");
            return workerType!;
        }

        private static EpisodeProvider CreateProvider(
            ILibraryManager libraryManager,
            IHttpContextAccessor httpContextAccessor,
            TmdbApi tmdbApi,
            IEpisodeTitleBackfillCandidateStore store,
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
                new TvdbApi(loggerFactory),
                store);
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

        private static void AssertLoggedMessage(TestLoggerProvider loggerProvider, LogLevel level, string categoryFragment, params string[] fragments)
        {
            var matches = loggerProvider.Messages.Any(message => message.LogLevel == level
                && message.Category.Contains(categoryFragment, StringComparison.Ordinal)
                && fragments.All(fragment => message.Message.Contains(fragment, StringComparison.Ordinal)));
            Assert.IsTrue(matches, $"Expected {level} log in {categoryFragment} containing fragments: {string.Join(", ", fragments)}");
        }

        private static object CreateGenericLoggerMock(Type targetType)
        {
            var loggerType = typeof(ILogger<>).MakeGenericType(targetType);
            var mockType = typeof(Mock<>).MakeGenericType(loggerType);
            var mock = Activator.CreateInstance(mockType);
            Assert.IsNotNull(mock, "Failed to create logger mock for deferred retry worker.");
            var objectProperty = mockType
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Single(property => property.Name == "Object" && property.PropertyType == loggerType);
            return objectProperty.GetValue(mock)!;
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

        private static EpisodeTitleBackfillCandidate CreatePathAwareCandidate(
            Guid itemId,
            string itemPath,
            DateTimeOffset queuedAtUtc,
            DateTimeOffset nextAttemptAtUtc,
            int attemptCount,
            DateTimeOffset? expiresAtUtc = null,
            string originalTitleSnapshot = "第 1 集",
            string candidateTitle = "皇后回宫")
        {
            var candidate = new EpisodeTitleBackfillCandidate
            {
                ItemId = itemId,
                OriginalTitleSnapshot = originalTitleSnapshot,
                CandidateTitle = candidateTitle,
                ExpiresAtUtc = expiresAtUtc ?? queuedAtUtc.AddMinutes(2),
            };

            SetRequiredProperty(candidate, "ItemPath", itemPath);
            SetRequiredProperty(candidate, "QueuedAtUtc", queuedAtUtc);
            SetRequiredProperty(candidate, "NextAttemptAtUtc", nextAttemptAtUtc);
            SetRequiredProperty(candidate, "AttemptCount", attemptCount);
            return candidate;
        }

        private static EpisodeTitleBackfillCandidate? PeekCandidateByPath(InMemoryEpisodeTitleBackfillCandidateStore candidateStore, string itemPath)
        {
            var peekByPathMethod = typeof(InMemoryEpisodeTitleBackfillCandidateStore).GetMethod("PeekByPath", new[] { typeof(string) });
            Assert.IsNotNull(peekByPathMethod, "Expected path-aware PeekByPath(string) API for deferred retry worker tests.");
            return peekByPathMethod!.Invoke(candidateStore, new object[] { itemPath }) as EpisodeTitleBackfillCandidate;
        }

        private static void SetRequiredProperty<T>(EpisodeTitleBackfillCandidate candidate, string propertyName, T value)
        {
            var property = typeof(EpisodeTitleBackfillCandidate).GetProperty(propertyName);
            Assert.IsNotNull(property, $"EpisodeTitleBackfillCandidate is missing required property '{propertyName}'.");
            property!.SetValue(candidate, value);
        }

        private static void SetFeatureEnabled(bool enabled)
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = enabled;
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
