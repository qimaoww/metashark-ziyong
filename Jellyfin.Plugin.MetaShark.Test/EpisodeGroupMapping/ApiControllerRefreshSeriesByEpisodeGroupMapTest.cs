using System.Globalization;
using System.Reflection;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Controllers;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Test.Logging;
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
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test.EpisodeGroupMapping
{
    [TestClass]
    [TestCategory("Stable")]
    public class ApiControllerRefreshSeriesByEpisodeGroupMapTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-episode-group-controller-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");

        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder => { });

        [TestMethod]
        public void RefreshSeriesByEpisodeGroupMap_SemanticNoOp_ReturnsNoOpSummaryAndDoesNotQueue()
        {
            using var harness = CreateHarness(
                items: new[]
                {
                    CreateSeries(Guid.NewGuid(), "Series 65942", "65942"),
                    CreateSeries(Guid.NewGuid(), "Series 70000", "70000"),
                });

            var result = harness.Controller.RefreshSeriesByEpisodeGroupMap(new TmdbEpisodeGroupRefreshRequest
            {
                OldMapping = "65942=group-a\n70000=group-b",
                NewMapping = "# comment\n70000 = group-b\n65942 = group-a",
            });

            Assert.AreEqual(1, result.Code);
            Assert.AreEqual(CreateExpectedSummary(queued: 0, affected: 0, added: 0, removed: 0, changed: 0, noOp: true), result.Msg);
            Assert.AreEqual(0, harness.QueueCalls.Count);
            harness.LibraryManagerStub.Verify(x => x.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
        }

        [TestMethod]
        public void RefreshSeriesByEpisodeGroupMap_AddMapping_QueuesOnlyNewlyAddedSeries()
        {
            var addedSeries = CreateSeries(Guid.NewGuid(), "Added series", "65942");
            var unchangedSeries = CreateSeries(Guid.NewGuid(), "Unchanged series", "70000");

            using var harness = CreateHarness(items: new[] { addedSeries, unchangedSeries });

            var result = harness.Controller.RefreshSeriesByEpisodeGroupMap(new TmdbEpisodeGroupRefreshRequest
            {
                OldMapping = "70000=group-b",
                NewMapping = "70000=group-b\n65942=group-a",
            });

            Assert.AreEqual(1, result.Code);
            Assert.AreEqual(CreateExpectedSummary(queued: 1, affected: 1, added: 1, removed: 0, changed: 0, noOp: false), result.Msg);
            AssertQueuedSeries(harness.QueueCalls, addedSeries.Id);
            LogAssert.AssertLoggedOnce(
                harness.LoggerStub,
                LogLevel.Information,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["Count"] = 1,
                },
                originalFormatContains: "[MetaShark] 已排队剧集组映射刷新",
                messageContains: ["[MetaShark] 已排队剧集组映射刷新", "Count=1"]);
        }

        [TestMethod]
        public void RefreshSeriesByEpisodeGroupMap_UpdateMapping_QueuesOnlyChangedSeries()
        {
            var changedSeries = CreateSeries(Guid.NewGuid(), "Changed series", "65942");
            var unchangedSeries = CreateSeries(Guid.NewGuid(), "Unchanged series", "70000");

            using var harness = CreateHarness(items: new[] { changedSeries, unchangedSeries });

            var result = harness.Controller.RefreshSeriesByEpisodeGroupMap(new TmdbEpisodeGroupRefreshRequest
            {
                OldMapping = "65942=old-group\n70000=group-b",
                NewMapping = "65942=new-group\n70000=group-b",
            });

            Assert.AreEqual(1, result.Code);
            Assert.AreEqual(CreateExpectedSummary(queued: 1, affected: 1, added: 0, removed: 0, changed: 1, noOp: false), result.Msg);
            AssertQueuedSeries(harness.QueueCalls, changedSeries.Id);
        }

        [TestMethod]
        public void RefreshSeriesByEpisodeGroupMap_DeleteMapping_QueuesRemovedSeries()
        {
            var removedSeries = CreateSeries(Guid.NewGuid(), "Removed series", "65942");
            var unchangedSeries = CreateSeries(Guid.NewGuid(), "Unchanged series", "70000");

            using var harness = CreateHarness(items: new[] { removedSeries, unchangedSeries });

            var result = harness.Controller.RefreshSeriesByEpisodeGroupMap(new TmdbEpisodeGroupRefreshRequest
            {
                OldMapping = "65942=group-a\n70000=group-b",
                NewMapping = "70000=group-b",
            });

            Assert.AreEqual(1, result.Code);
            Assert.AreEqual(CreateExpectedSummary(queued: 1, affected: 1, added: 0, removed: 1, changed: 0, noOp: false), result.Msg);
            AssertQueuedSeries(harness.QueueCalls, removedSeries.Id);
        }

        [TestMethod]
        public void RefreshSeriesByEpisodeGroupMap_MissingBody_FallsBackToCurrentConfiguration()
        {
            using var harness = CreateHarness(
                items: new[]
                {
                    CreateSeries(Guid.NewGuid(), "Series 65942", "65942"),
                    CreateSeries(Guid.NewGuid(), "Series 70000", "70000"),
                },
                currentMapping:
                """
                invalid-line
                65942=group-a
                70000 = group-b
                """);

            var result = harness.Controller.RefreshSeriesByEpisodeGroupMap();

            Assert.AreEqual(1, result.Code);
            Assert.AreEqual(CreateExpectedSummary(queued: 2, affected: 2, added: 2, removed: 0, changed: 0, newInvalid: 1, noOp: false), result.Msg);
            AssertQueuedSeries(harness.QueueCalls, harness.Items.Select(x => x.Id).ToArray());
        }

        [TestMethod]
        public void RefreshSeriesByEpisodeGroupMap_EmptyGuidItem_SkipsQueueWithoutThrowing()
        {
            var seriesWithEmptyId = CreateSeries(Guid.Empty, "Series with empty Id", "65942");

            using var harness = CreateHarness(items: new[] { seriesWithEmptyId });

            var result = harness.Controller.RefreshSeriesByEpisodeGroupMap(new TmdbEpisodeGroupRefreshRequest
            {
                OldMapping = string.Empty,
                NewMapping = "65942=group-a",
            });

            Assert.AreEqual(1, result.Code);
            Assert.AreEqual(CreateExpectedSummary(queued: 0, affected: 1, added: 1, removed: 0, changed: 0, noOp: false), result.Msg);
            Assert.AreEqual(0, harness.QueueCalls.Count);
            LogAssert.AssertLoggedOnce(
                harness.LoggerStub,
                LogLevel.Warning,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["Name"] = "Series with empty Id",
                },
                originalFormatContains: "[MetaShark] 跳过剧集组映射刷新",
                messageContains: ["[MetaShark] 跳过剧集组映射刷新", "reason=EmptyId", "name=Series with empty Id"]);
        }

        [TestMethod]
        public void RefreshSeriesByEpisodeGroupMap_MultipleSeriesWithSameTmdbId_QueuesAllMatches()
        {
            var firstMatch = CreateSeries(Guid.NewGuid(), "Series duplicate A", "65942");
            var secondMatch = CreateSeries(Guid.NewGuid(), "Series duplicate B", "65942");
            var unaffected = CreateSeries(Guid.NewGuid(), "Series unaffected", "70000");

            using var harness = CreateHarness(items: new[] { firstMatch, secondMatch, unaffected });

            var result = harness.Controller.RefreshSeriesByEpisodeGroupMap(new TmdbEpisodeGroupRefreshRequest
            {
                OldMapping = string.Empty,
                NewMapping = "65942=group-a",
            });

            Assert.AreEqual(1, result.Code);
            Assert.AreEqual(CreateExpectedSummary(queued: 2, affected: 1, added: 1, removed: 0, changed: 0, noOp: false), result.Msg);
            AssertQueuedSeries(harness.QueueCalls, firstMatch.Id, secondMatch.Id);
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

        private static string CreateExpectedSummary(
            int queued,
            int affected,
            int added,
            int removed,
            int changed,
            bool noOp,
            int oldInvalid = 0,
            int newInvalid = 0,
            int oldDuplicate = 0,
            int newDuplicate = 0)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Episode group refresh summary: queued={0}, affected={1} (added={2}, removed={3}, changed={4}), invalid(old/new)={5}/{6}, duplicate(old/new)={7}/{8}, no-op={9}.",
                queued,
                affected,
                added,
                removed,
                changed,
                oldInvalid,
                newInvalid,
                oldDuplicate,
                newDuplicate,
                noOp ? "yes" : "no");
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

        private ControllerHarness CreateHarness(IEnumerable<BaseItem> items, string currentMapping = "")
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(new PluginConfiguration
            {
                TmdbEpisodeGroupMap = currentMapping,
            });

            var materializedItems = items.ToList();
            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(materializedItems);

            var queueCalls = new List<QueueRefreshCall>();
            var providerManagerStub = new Mock<IProviderManager>();
            providerManagerStub
                .Setup(x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()))
                .Callback<Guid, MetadataRefreshOptions, RefreshPriority>((itemId, options, priority) => queueCalls.Add(new QueueRefreshCall(itemId, options, priority)));

            var loggerStub = new Mock<ILogger<ApiController>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var controller = new ApiController(
                new Mock<IHttpClientFactory>().Object,
                new DoubanApi(this.loggerFactory),
                libraryManagerStub.Object,
                providerManagerStub.Object,
                new Mock<IFileSystem>().Object,
                loggerStub.Object);

            return new ControllerHarness(controller, materializedItems, libraryManagerStub, queueCalls, loggerStub);
        }

        private sealed record QueueRefreshCall(Guid ItemId, MetadataRefreshOptions Options, RefreshPriority Priority);

        private sealed class ControllerHarness : IDisposable
        {
            public ControllerHarness(
                ApiController controller,
                IReadOnlyList<BaseItem> items,
                Mock<ILibraryManager> libraryManagerStub,
                IReadOnlyCollection<QueueRefreshCall> queueCalls,
                Mock<ILogger<ApiController>> loggerStub)
            {
                this.Controller = controller;
                this.Items = items;
                this.LibraryManagerStub = libraryManagerStub;
                this.QueueCalls = queueCalls;
                this.LoggerStub = loggerStub;
            }

            public ApiController Controller { get; }

            public IReadOnlyList<BaseItem> Items { get; }

            public Mock<ILibraryManager> LibraryManagerStub { get; }

            public IReadOnlyCollection<QueueRefreshCall> QueueCalls { get; }

            public Mock<ILogger<ApiController>> LoggerStub { get; }

            public void Dispose()
            {
                ReplacePluginConfiguration(new PluginConfiguration());
            }
        }
    }
}
