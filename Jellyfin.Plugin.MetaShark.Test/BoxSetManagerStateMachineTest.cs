using Jellyfin.Plugin.MetaShark.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Moq;
using Jellyfin.Data.Enums;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [DoNotParallelize]
    [TestCategory("Stable")]
    public class BoxSetManagerStateMachineTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-boxset-state-machine-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");

        [TestInitialize]
        public void SetUp()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableTmdbCollection = true,
            });
        }

        [TestCleanup]
        public void TearDown()
        {
            ReplacePluginConfiguration(new PluginConfiguration());
        }

        [TestMethod]
        public async Task ItemUpdated_DebouncesDuplicateCollectionUpdatesIntoSingleDrain()
        {
            var harness = CreateHarness(("Saga A", Guid.NewGuid()));
            var addCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.CollectionManager
                .Setup(x => x.AddToCollectionAsync(
                    harness.BoxSets["Saga A"].Id,
                    It.IsAny<IEnumerable<Guid>>()))
                .Callback(() => addCompleted.TrySetResult())
                .Returns(Task.CompletedTask);

            using var manager = harness.CreateManager();
            await manager.StartAsync(CancellationToken.None).ConfigureAwait(false);

            harness.RaiseMovieUpdated("Saga A");
            harness.RaiseMovieUpdated("Saga A");

            await harness.Scheduler.FireAsync().ConfigureAwait(false);
            await addCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            harness.CollectionManager.Verify(
                x => x.AddToCollectionAsync(harness.BoxSets["Saga A"].Id, It.IsAny<IEnumerable<Guid>>()),
                Times.Once);
            await manager.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        [TestMethod]
        public async Task Drain_WhenCollectionArrivesDuringInFlight_LeavesItPendingAndRunsNextDrain()
        {
            var harness = CreateHarness(("Saga A", Guid.NewGuid()), ("Saga B", Guid.NewGuid()));
            var firstDrainStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondDrainCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondDrainAllowed = false;

            harness.CollectionManager
                .Setup(x => x.AddToCollectionAsync(harness.BoxSets["Saga A"].Id, It.IsAny<IEnumerable<Guid>>()))
                .Callback(() => firstDrainStarted.TrySetResult())
                .Returns(async () => await releaseFirstDrain.Task.ConfigureAwait(false));
            harness.CollectionManager
                .Setup(x => x.AddToCollectionAsync(harness.BoxSets["Saga B"].Id, It.IsAny<IEnumerable<Guid>>()))
                .Callback(() =>
                {
                    Assert.IsTrue(secondDrainAllowed, "Saga B 必须由第二轮 drain 处理，不能在第一轮 drain 内提前处理。");
                    secondDrainCompleted.TrySetResult();
                })
                .Returns(Task.CompletedTask);

            using var manager = harness.CreateManager();
            await manager.StartAsync(CancellationToken.None).ConfigureAwait(false);

            harness.RaiseMovieUpdated("Saga A");
            var firstDrain = harness.Scheduler.FireAsync();
            await firstDrainStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            harness.RaiseMovieUpdated("Saga B");
            Assert.IsFalse(secondDrainCompleted.Task.IsCompleted, "Saga B 不应在第一轮 drain 期间被处理。");
            harness.CollectionManager.Verify(
                x => x.AddToCollectionAsync(harness.BoxSets["Saga B"].Id, It.IsAny<IEnumerable<Guid>>()),
                Times.Never);
            releaseFirstDrain.SetResult();
            await firstDrain.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            Assert.IsTrue(harness.Scheduler.HasScheduledCallback, "drain 中新增的 collection 应留在 pending 并重新安排 debounce。");
            secondDrainAllowed = true;
            await harness.Scheduler.FireAsync().ConfigureAwait(false);
            await secondDrainCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Assert.IsFalse(harness.Scheduler.HasScheduledCallback, "第二轮成功后不应残留新的 scheduled callback。");

            harness.CollectionManager.Verify(
                x => x.AddToCollectionAsync(harness.BoxSets["Saga A"].Id, It.IsAny<IEnumerable<Guid>>()),
                Times.Once);
            harness.CollectionManager.Verify(
                x => x.AddToCollectionAsync(harness.BoxSets["Saga B"].Id, It.IsAny<IEnumerable<Guid>>()),
                Times.Once);
            await manager.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        [TestMethod]
        public async Task Drain_WhenAddToCollectionFails_RequeuesCollectionForRetry()
        {
            var harness = CreateHarness(("Saga A", Guid.NewGuid()));
            var retryCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attemptCount = 0;
            harness.CollectionManager
                .Setup(x => x.AddToCollectionAsync(
                    harness.BoxSets["Saga A"].Id,
                    It.IsAny<IEnumerable<Guid>>()))
                .Returns(() =>
                {
                    attemptCount++;
                    if (attemptCount == 1)
                    {
                        return Task.FromException(new IOException("transient collection update failure"));
                    }

                    retryCompleted.TrySetResult();
                    return Task.CompletedTask;
                });

            using var manager = harness.CreateManager();
            await manager.StartAsync(CancellationToken.None).ConfigureAwait(false);

            harness.RaiseMovieUpdated("Saga A");
            await harness.Scheduler.FireAsync().ConfigureAwait(false);

            Assert.AreEqual(1, attemptCount);
            Assert.IsTrue(harness.Scheduler.HasScheduledCallback, "失败 collection 应回到 pending 并重新安排 debounce。");
            await harness.Scheduler.FireAsync().ConfigureAwait(false);
            await retryCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            Assert.AreEqual(2, attemptCount);
            Assert.IsFalse(harness.Scheduler.HasScheduledCallback, "成功重试后不应残留新的 scheduled callback。");
            await manager.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        [TestMethod]
        public async Task StopAsync_WaitsForCurrentDrainAndStopsFutureDrains()
        {
            var harness = CreateHarness(("Saga A", Guid.NewGuid()));
            var drainStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            harness.CollectionManager
                .Setup(x => x.AddToCollectionAsync(
                    harness.BoxSets["Saga A"].Id,
                    It.IsAny<IEnumerable<Guid>>()))
                .Callback(() => drainStarted.TrySetResult())
                .Returns(async () => await releaseDrain.Task.ConfigureAwait(false));

            using var manager = harness.CreateManager();
            await manager.StartAsync(CancellationToken.None).ConfigureAwait(false);

            harness.RaiseMovieUpdated("Saga A");
            Assert.IsTrue(harness.Scheduler.HasScheduledCallback, "更新后应安排 debounce 回调。");
            var drain = harness.Scheduler.FireAsync();
            await drainStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            var stopTask = manager.StopAsync(CancellationToken.None);
            Assert.IsFalse(stopTask.IsCompleted, "StopAsync 应等待当前 drain 自然结束。");
            releaseDrain.SetResult();
            await drain.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await stopTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            harness.RaiseMovieUpdated("Saga A");
            Assert.IsFalse(harness.Scheduler.HasScheduledCallback, "停止后不应再保留新的 scheduled callback。");

            await harness.Scheduler.FireAsync().ConfigureAwait(false);
            await Task.Delay(100).ConfigureAwait(false);

            harness.CollectionManager.Verify(
                x => x.AddToCollectionAsync(harness.BoxSets["Saga A"].Id, It.IsAny<IEnumerable<Guid>>()),
                Times.Once);
        }

        [TestMethod]
        public async Task StopAsync_DropsCapturedTimerCallbackAfterStop()
        {
            var harness = CreateHarness(("Saga A", Guid.NewGuid()));
            Func<Task>? scheduledCallback;

            using var manager = harness.CreateManager();
            await manager.StartAsync(CancellationToken.None).ConfigureAwait(false);

            harness.RaiseMovieUpdated("Saga A");
            Assert.IsTrue(harness.Scheduler.HasScheduledCallback, "更新后应先排队 debounce 回调。");
            scheduledCallback = harness.Scheduler.TakeScheduledCallback();
            Assert.IsNotNull(scheduledCallback);
            Assert.IsFalse(harness.Scheduler.HasScheduledCallback, "取出 pending callback 后，scheduler 应该清空。");

            var stopTask = manager.StopAsync(CancellationToken.None);
            await stopTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

            await scheduledCallback!().ConfigureAwait(false);
            Assert.IsFalse(harness.Scheduler.HasScheduledCallback, "停止后执行已取出的 callback 也不应重新排队。");

            harness.CollectionManager.Verify(
                x => x.AddToCollectionAsync(harness.BoxSets["Saga A"].Id, It.IsAny<IEnumerable<Guid>>()),
                Times.Never);
        }

        private static Harness CreateHarness(params (string CollectionName, Guid BoxSetId)[] collections)
        {
            return new Harness(collections);
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
            if (MetaSharkPlugin.Instance?.Configuration == null)
            {
                ReplacePluginConfiguration(new PluginConfiguration());
            }
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

        private sealed class Harness
        {
            private readonly Mock<ILibraryManager> libraryManager;
            private readonly Dictionary<string, List<Movie>> moviesByCollection = new();

            public Harness((string CollectionName, Guid BoxSetId)[] collections)
            {
                this.libraryManager = new Mock<ILibraryManager>();
                this.CollectionManager = new Mock<ICollectionManager>();
                this.Scheduler = new ManualBoxSetDebounceScheduler();
                this.LibraryFolder = new Folder
                {
                    Name = "Movies",
                    Path = "/library/movies",
                };
                this.RootFolder = new Mock<AggregateFolder>();
                this.RootFolder.SetupGet(x => x.Children).Returns(new BaseItem[] { this.LibraryFolder });
                this.libraryManager.SetupGet(x => x.RootFolder).Returns(this.RootFolder.Object);
                this.libraryManager
                    .Setup(x => x.GetLibraryOptions(It.IsAny<BaseItem>()))
                    .Returns(CreateMovieLibraryOptions(metadataAllowed: true));
                this.BoxSets = collections.ToDictionary(
                    collection => collection.CollectionName,
                    collection => new BoxSet
                    {
                        Id = collection.BoxSetId,
                        Name = collection.CollectionName,
                    });

                foreach (var collection in collections)
                {
                    this.moviesByCollection[collection.CollectionName] = new List<Movie>
                    {
                        CreateMovie(collection.CollectionName, "A"),
                        CreateMovie(collection.CollectionName, "B"),
                    };
                }

                this.libraryManager
                    .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                    .Returns((InternalItemsQuery query) =>
                    {
                        if (query.IncludeItemTypes?.Contains(BaseItemKind.BoxSet) == true)
                        {
                            return this.BoxSets.Values.Cast<BaseItem>().ToList();
                        }

                        if (query.IncludeItemTypes?.Contains(BaseItemKind.Movie) == true)
                        {
                            return this.moviesByCollection.Values.SelectMany(movies => movies).Cast<BaseItem>().ToList();
                        }

                        return new List<BaseItem>();
                    });
            }

            public Mock<ICollectionManager> CollectionManager { get; }

            public ManualBoxSetDebounceScheduler Scheduler { get; }

            public Dictionary<string, BoxSet> BoxSets { get; }

            private Folder LibraryFolder { get; }

            private Mock<AggregateFolder> RootFolder { get; }

            public BoxSetManager CreateManager()
            {
                var loggerFactoryStub = new Mock<ILoggerFactory>();
                loggerFactoryStub.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(Mock.Of<ILogger>());
                return new BoxSetManager(
                    this.libraryManager.Object,
                    this.CollectionManager.Object,
                    loggerFactoryStub.Object,
                    TimeSpan.FromMinutes(1),
                    this.Scheduler);
            }

            public void RaiseMovieUpdated(string collectionName)
            {
                this.libraryManager.Raise(
                    x => x.ItemUpdated += null,
                    this.libraryManager.Object,
                    new ItemChangeEventArgs
                    {
                        Item = this.moviesByCollection[collectionName][0],
                        UpdateReason = ItemUpdateType.MetadataDownload,
                    });
            }

            private static Movie CreateMovie(string collectionName, string suffix)
            {
                var movieStub = new Mock<Movie> { CallBase = true };
                movieStub.SetupGet(x => x.LocationType).Returns((dynamic)CreateNonVirtualLocationTypeValue());
                movieStub.Object.Id = Guid.NewGuid();
                movieStub.Object.Name = $"{collectionName} Movie {suffix}";
                movieStub.Object.Path = $"/library/movies/{collectionName}/movie-{suffix}.mkv";
                movieStub.Object.CollectionName = collectionName;
                movieStub.Object.ProviderIds = new Dictionary<string, string>
                {
                    ["TmdbCollection"] = collectionName,
                };

                return movieStub.Object;
            }

            private static object CreateNonVirtualLocationTypeValue()
            {
                var locationTypeProperty = typeof(Movie).GetProperty(nameof(Movie.LocationType));
                Assert.IsNotNull(locationTypeProperty);

                var nonVirtualLocationType = Enum.GetValues(locationTypeProperty!.PropertyType)
                    .Cast<object>()
                    .FirstOrDefault(value => !string.Equals(value.ToString(), "Virtual", StringComparison.Ordinal));
                Assert.IsNotNull(nonVirtualLocationType, "无法解析非 Virtual 的 LocationType 枚举值。");
                return nonVirtualLocationType!;
            }

            private static LibraryOptions CreateMovieLibraryOptions(bool metadataAllowed)
            {
                return new LibraryOptions
                {
                    TypeOptions = new[]
                    {
                        new TypeOptions
                        {
                            Type = nameof(Movie),
                            MetadataFetchers = metadataAllowed ? new[] { MetaSharkPlugin.PluginName } : Array.Empty<string>(),
                            ImageFetchers = Array.Empty<string>(),
                        },
                    },
                };
            }
        }

        private sealed class ManualBoxSetDebounceScheduler : IBoxSetDebounceScheduler
        {
            private Func<Task>? callback;

            public bool HasScheduledCallback => this.callback != null;

            public void Schedule(TimeSpan delay, Func<Task> callback)
            {
                ArgumentNullException.ThrowIfNull(callback);
                this.callback = callback;
            }

            public void Cancel()
            {
                this.callback = null;
            }

            public Task FireAsync()
            {
                var scheduledCallback = this.callback;
                this.callback = null;
                return scheduledCallback == null ? Task.CompletedTask : scheduledCallback();
            }

            public Func<Task>? TakeScheduledCallback()
            {
                var scheduledCallback = this.callback;
                this.callback = null;
                return scheduledCallback;
            }

            public void Dispose()
            {
            }
        }
    }
}
