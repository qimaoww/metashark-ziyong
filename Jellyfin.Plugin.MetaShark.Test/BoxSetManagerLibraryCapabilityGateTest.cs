using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MetaShark.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [DoNotParallelize]
    public class BoxSetManagerLibraryCapabilityGateTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-boxset-manager-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");

        [TestInitialize]
        public void ResetConfigurationBeforeTest()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(new PluginConfiguration());
        }

        [TestCleanup]
        public void ResetConfigurationAfterTest()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(new PluginConfiguration());
        }

        [TestMethod]
        public void GetMoviesFromLibrary_OnlyProcessesMoviesFromMetadataEnabledLibraries()
        {
            var enabledLibrary = new Folder { Name = "Enabled Movies", Path = "/library/enabled-movies" };
            var disabledLibrary = new Folder { Name = "Disabled Movies", Path = "/library/disabled-movies" };
            var rootFolderStub = new Mock<AggregateFolder>();
            rootFolderStub.SetupGet(x => x.Children).Returns(new BaseItem[] { enabledLibrary, disabledLibrary });

            var enabledMovie = CreateMovie("Enabled Movie", "/library/enabled-movies/movie-a.mkv", "Enabled Saga");
            var disabledMovie = CreateItemUpdatedMovie("Disabled Movie", "/library/disabled-movies/movie-b.mkv", "Disabled Saga");

            var libraryOptionsByItem = new Dictionary<BaseItem, LibraryOptions?>
            {
                [enabledLibrary] = CreateMovieLibraryOptions(metadataAllowed: true),
                [disabledLibrary] = CreateMovieLibraryOptions(metadataAllowed: false),
                [enabledMovie] = CreateMovieLibraryOptions(metadataAllowed: true),
                [disabledMovie] = CreateMovieLibraryOptions(metadataAllowed: false),
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub.SetupGet(x => x.RootFolder).Returns(rootFolderStub.Object);
            libraryManagerStub
                .SetupSequence(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { enabledMovie })
                .Returns(new List<BaseItem> { disabledMovie });
            libraryManagerStub
                .Setup(x => x.GetLibraryOptions(It.IsAny<BaseItem>()))
                .Returns((BaseItem item) => libraryOptionsByItem.TryGetValue(item, out var libraryOptions) ? libraryOptions! : null!);

            using var manager = CreateManager(libraryManagerStub.Object);

            var collectionMoviesMap = manager.GetMoviesFromLibrary();

            Assert.AreEqual(1, collectionMoviesMap.Count);
            Assert.IsTrue(collectionMoviesMap.TryGetValue("Enabled Saga", out var enabledCollectionMovies));
            Assert.AreEqual(1, enabledCollectionMovies!.Count);
            Assert.AreSame(enabledMovie, enabledCollectionMovies[0]);
            Assert.IsFalse(collectionMoviesMap.ContainsKey("Disabled Saga"));
            libraryManagerStub.Verify(x => x.GetLibraryOptions(It.Is<BaseItem>(item => ReferenceEquals(item, enabledMovie))), Times.AtLeastOnce);
            libraryManagerStub.Verify(x => x.GetLibraryOptions(It.Is<BaseItem>(item => ReferenceEquals(item, disabledMovie))), Times.AtLeastOnce);
        }

        [TestMethod]
        public async Task StartAsync_ItemUpdated_DoesNotQueueCollectionForMetadataDisabledMovieLibrary()
        {
            ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableTmdbCollection = true,
            });

            var disabledMovie = CreateItemUpdatedMovie("Disabled Movie", "/library/disabled-movies/movie-b.mkv", "Disabled Saga");
            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetLibraryOptions(It.IsAny<BaseItem>()))
                .Returns((BaseItem item) => ReferenceEquals(item, disabledMovie) ? CreateMovieLibraryOptions(metadataAllowed: false) : null!);

            using var manager = CreateManager(libraryManagerStub.Object);
            await manager.StartAsync(CancellationToken.None).ConfigureAwait(false);

            libraryManagerStub.Raise(
                x => x.ItemUpdated += null,
                libraryManagerStub.Object,
                new ItemChangeEventArgs
                {
                    Item = disabledMovie,
                    UpdateReason = ItemUpdateType.MetadataDownload,
                });

            CollectionAssert.AreEqual(Array.Empty<string>(), GetQueuedTmdbCollections(manager).ToArray());
            await manager.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        [TestMethod]
        public async Task StartAsync_ItemUpdated_QueuesCollectionForMetadataEnabledMovieLibrary()
        {
            ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableTmdbCollection = true,
            });

            var enabledMovie = CreateItemUpdatedMovie("Enabled Movie", "/library/enabled-movies/movie-a.mkv", "Enabled Saga");
            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetLibraryOptions(It.IsAny<BaseItem>()))
                .Returns((BaseItem item) => ReferenceEquals(item, enabledMovie) ? CreateMovieLibraryOptions(metadataAllowed: true) : null!);

            using var manager = CreateManager(libraryManagerStub.Object);
            await manager.StartAsync(CancellationToken.None).ConfigureAwait(false);

            libraryManagerStub.Raise(
                x => x.ItemUpdated += null,
                libraryManagerStub.Object,
                new ItemChangeEventArgs
                {
                    Item = enabledMovie,
                    UpdateReason = ItemUpdateType.MetadataDownload,
                });

            CollectionAssert.AreEquivalent(new[] { "Enabled Saga" }, GetQueuedTmdbCollections(manager).ToArray());
            await manager.StopAsync(CancellationToken.None).ConfigureAwait(false);
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

        private static Movie CreateMovie(string name, string path, string collectionName)
        {
            return new Movie
            {
                Id = Guid.NewGuid(),
                Name = name,
                Path = path,
                CollectionName = collectionName,
                ProviderIds = new Dictionary<string, string>
                {
                    ["TmdbCollection"] = collectionName,
                },
            };
        }

        private static Movie CreateItemUpdatedMovie(string name, string path, string collectionName)
        {
            var movieStub = new Mock<Movie> { CallBase = true };
            movieStub.SetupGet(x => x.LocationType).Returns((dynamic)CreateNonVirtualLocationTypeValue());

            movieStub.Object.Id = Guid.NewGuid();
            movieStub.Object.Name = name;
            movieStub.Object.Path = path;
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

        private static BoxSetManager CreateManager(ILibraryManager libraryManager)
        {
            var loggerFactoryStub = new Mock<ILoggerFactory>();
            loggerFactoryStub.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(Mock.Of<ILogger>());
            return new BoxSetManager(libraryManager, Mock.Of<ICollectionManager>(), loggerFactoryStub.Object);
        }

        private static IReadOnlyCollection<string> GetQueuedTmdbCollections(BoxSetManager manager)
        {
            var queueField = typeof(BoxSetManager).GetField("queuedTmdbCollection", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(queueField);

            var queuedCollections = queueField!.GetValue(manager) as HashSet<string>;
            Assert.IsNotNull(queuedCollections);
            return queuedCollections!.ToArray();
        }
    }
}
