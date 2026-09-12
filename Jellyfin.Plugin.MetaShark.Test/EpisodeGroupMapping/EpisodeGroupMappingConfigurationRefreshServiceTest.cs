using System.Reflection;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.EpisodeGroupMapping;
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
    [DoNotParallelize]
    [TestCategory("Stable")]
    public class EpisodeGroupMappingConfigurationRefreshServiceTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-episode-group-configuration-refresh-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");

        [TestInitialize]
        public void ResetBeforeEachTest()
        {
            if (Directory.Exists(PluginTestRootPath))
            {
                Directory.Delete(PluginTestRootPath, recursive: true);
            }

            RecreatePluginInstance();
            ReplacePluginConfiguration(new PluginConfiguration());
        }

        [TestCleanup]
        public void ResetAfterEachTest()
        {
            ReplacePluginConfiguration(new PluginConfiguration());
        }

        [TestMethod]
        public async Task UpdateConfiguration_WhenManualEpisodeGroupMappingAdded_QueuesAffectedMetadataRefreshWithoutApiPost()
        {
            ReplacePluginConfiguration(new PluginConfiguration());
            var series = CreateSeries(Guid.NewGuid(), "Series 65942", "65942");
            var season = CreateSeason(Guid.NewGuid(), "Season 4", series.Id);
            var episode = CreateEpisode(Guid.NewGuid(), "Episode 10", series.Id);
            var harness = CreateHarness(
                new[] { series },
                seasonsBySeriesId: new Dictionary<Guid, IReadOnlyList<BaseItem>>
                {
                    [series.Id] = new BaseItem[] { season },
                },
                episodesBySeriesId: new Dictionary<Guid, IReadOnlyList<BaseItem>>
                {
                    [series.Id] = new BaseItem[] { episode },
                });
            await harness.Service.StartAsync(CancellationToken.None).ConfigureAwait(false);

            MetaSharkPlugin.Instance!.UpdateConfiguration(new PluginConfiguration
            {
                TmdbEpisodeGroupMap = "65942=manual-group",
            });

            await harness.Service.StopAsync(CancellationToken.None).ConfigureAwait(false);
            AssertQueuedMetadataRefreshes(harness.QueueCalls, season.Id, episode.Id);
        }

        [TestMethod]
        public async Task UpdateConfiguration_WhenManualMappingOverridesLlmMapping_QueuesChangedEffectiveMapping()
        {
            ReplacePluginConfiguration(new PluginConfiguration
            {
                LlmTmdbEpisodeGroupMap = "65942=llm-group",
            });
            var series = CreateSeries(Guid.NewGuid(), "Series 65942", "65942");
            var episode = CreateEpisode(Guid.NewGuid(), "Episode 10", series.Id);
            var harness = CreateHarness(
                new[] { series },
                episodesBySeriesId: new Dictionary<Guid, IReadOnlyList<BaseItem>>
                {
                    [series.Id] = new BaseItem[] { episode },
                });
            await harness.Service.StartAsync(CancellationToken.None).ConfigureAwait(false);

            MetaSharkPlugin.Instance!.UpdateConfiguration(new PluginConfiguration
            {
                TmdbEpisodeGroupMap = "65942=manual-group",
                LlmTmdbEpisodeGroupMap = "65942=llm-group",
            });

            await harness.Service.StopAsync(CancellationToken.None).ConfigureAwait(false);
            AssertQueuedMetadataRefreshes(harness.QueueCalls, episode.Id);
        }

        [TestMethod]
        public async Task UpdateConfiguration_WhenEffectiveMappingIsUnchanged_DoesNotQueueRefresh()
        {
            ReplacePluginConfiguration(new PluginConfiguration
            {
                TmdbEpisodeGroupMap = "65942=manual-group",
                LlmTmdbEpisodeGroupMap = "70000=llm-group",
            });
            var series = CreateSeries(Guid.NewGuid(), "Series 65942", "65942");
            var harness = CreateHarness(new[] { series });
            await harness.Service.StartAsync(CancellationToken.None).ConfigureAwait(false);

            MetaSharkPlugin.Instance!.UpdateConfiguration(new PluginConfiguration
            {
                TmdbEpisodeGroupMap = "# comment\n65942 = manual-group",
                LlmTmdbEpisodeGroupMap = "70000=llm-group",
            });

            await harness.Service.StopAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(0, harness.QueueCalls.Count);
        }

        [TestMethod]
        public async Task SynchronizeEffectiveMappingBaseline_ThenUpdateConfiguration_DoesNotQueueRefreshAgain()
        {
            var series = CreateSeries(Guid.NewGuid(), "Series 65942", "65942");
            var episode = CreateEpisode(Guid.NewGuid(), "Episode 10", series.Id);
            var harness = CreateHarness(
                new[] { series },
                episodesBySeriesId: new Dictionary<Guid, IReadOnlyList<BaseItem>>
                {
                    [series.Id] = new BaseItem[] { episode },
                });
            await harness.Service.StartAsync(CancellationToken.None).ConfigureAwait(false);

            // 模拟 LLM 写入映射：它已经自行刷新过，并把基线同步到新映射。
            harness.Service.SynchronizeEffectiveMappingBaseline("65942=llm-group");
            ReplacePluginConfiguration(new PluginConfiguration
            {
                LlmTmdbEpisodeGroupMap = "65942=llm-group",
            });

            MetaSharkPlugin.Instance!.UpdateConfiguration(new PluginConfiguration
            {
                LlmTmdbEpisodeGroupMap = "65942=llm-group",
            });

            await harness.Service.StopAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(0, harness.QueueCalls.Count, "基线已同步的映射变更不应再次整队刷新。");
        }

        [TestMethod]
        public async Task UpdateConfiguration_WhenRefreshQueueFails_RetainsPreviousMappingForRetry()
        {
            ReplacePluginConfiguration(new PluginConfiguration());
            var series = CreateSeries(Guid.NewGuid(), "Series 65942", "65942");
            var episode = CreateEpisode(Guid.NewGuid(), "Episode 10", series.Id);
            var harness = CreateHarness(
                new[] { series },
                episodesBySeriesId: new Dictionary<Guid, IReadOnlyList<BaseItem>>
                {
                    [series.Id] = new BaseItem[] { episode },
                },
                throwFirstSeriesQuery: true);
            await harness.Service.StartAsync(CancellationToken.None).ConfigureAwait(false);

            MetaSharkPlugin.Instance!.UpdateConfiguration(new PluginConfiguration
            {
                TmdbEpisodeGroupMap = "65942=manual-group",
            });

            // 等第一次（注定失败）的刷新处理完，再验证失败后基线未被推进。
            await harness.Service.WaitForPendingRefreshAsync().ConfigureAwait(false);
            Assert.AreEqual(0, harness.QueueCalls.Count);

            MetaSharkPlugin.Instance.UpdateConfiguration(new PluginConfiguration
            {
                TmdbEpisodeGroupMap = "65942=manual-group",
            });

            await harness.Service.StopAsync(CancellationToken.None).ConfigureAwait(false);
            AssertQueuedMetadataRefreshes(harness.QueueCalls, episode.Id);
        }

        private static ServiceHarness CreateHarness(
            IEnumerable<BaseItem> seriesItems,
            IReadOnlyDictionary<Guid, IReadOnlyList<BaseItem>>? seasonsBySeriesId = null,
            IReadOnlyDictionary<Guid, IReadOnlyList<BaseItem>>? episodesBySeriesId = null,
            bool throwFirstSeriesQuery = false)
        {
            var materializedSeriesItems = seriesItems.ToList();
            var materializedSeasonsBySeriesId = seasonsBySeriesId ?? new Dictionary<Guid, IReadOnlyList<BaseItem>>();
            var materializedEpisodesBySeriesId = episodesBySeriesId ?? new Dictionary<Guid, IReadOnlyList<BaseItem>>();
            var seriesQueryAttempts = 0;
            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.Is<InternalItemsQuery>(query => IsSeriesQuery(query))))
                .Returns<InternalItemsQuery>(_ =>
                {
                    if (throwFirstSeriesQuery && seriesQueryAttempts++ == 0)
                    {
                        throw new InvalidOperationException("Transient series query failure.");
                    }

                    return materializedSeriesItems;
                });
            libraryManagerStub
                .Setup(x => x.GetItemList(It.Is<InternalItemsQuery>(query => HasSingleIncludeItemType(query, BaseItemKind.Season))))
                .Returns<InternalItemsQuery>(query =>
                    query.AncestorIds.Length == 1
                    && materializedSeasonsBySeriesId.TryGetValue(query.AncestorIds[0], out var seasons)
                        ? seasons.ToList()
                        : new List<BaseItem>());
            libraryManagerStub
                .Setup(x => x.GetItemList(It.Is<InternalItemsQuery>(query => HasSingleIncludeItemType(query, BaseItemKind.Episode))))
                .Returns<InternalItemsQuery>(query =>
                    query.AncestorIds.Length == 1
                    && materializedEpisodesBySeriesId.TryGetValue(query.AncestorIds[0], out var episodes)
                        ? episodes.ToList()
                        : new List<BaseItem>());

            var queueCalls = new List<QueueRefreshCall>();
            var providerManagerStub = new Mock<IProviderManager>();
            providerManagerStub
                .Setup(x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()))
                .Callback<Guid, MetadataRefreshOptions, RefreshPriority>((itemId, options, priority) => queueCalls.Add(new QueueRefreshCall(itemId, options, priority)));
            var fileSystemStub = new Mock<IFileSystem>();
            var coordinator = new EpisodeGroupRefreshCoordinator(
                libraryManagerStub.Object,
                providerManagerStub.Object,
                fileSystemStub.Object,
                new EpisodeGroupRefreshService());
            var service = new EpisodeGroupMappingConfigurationRefreshService(
                new EpisodeGroupMappingFacade(),
                coordinator,
                Mock.Of<ILogger<EpisodeGroupMappingConfigurationRefreshService>>());

            return new ServiceHarness(service, queueCalls);
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

        private static Season CreateSeason(Guid id, string name, Guid seriesId)
        {
            return new Season
            {
                Id = id,
                Name = name,
                SeriesId = seriesId,
            };
        }

        private static Episode CreateEpisode(Guid id, string name, Guid seriesId)
        {
            return new Episode
            {
                Id = id,
                Name = name,
                SeriesId = seriesId,
            };
        }

        private static void AssertQueuedMetadataRefreshes(IReadOnlyCollection<QueueRefreshCall> queueCalls, params Guid[] expectedIds)
        {
            CollectionAssert.AreEquivalent(expectedIds, queueCalls.Select(x => x.ItemId).ToArray());

            foreach (var queueCall in queueCalls)
            {
                Assert.AreEqual(RefreshPriority.High, queueCall.Priority);
                Assert.AreEqual(MetadataRefreshMode.FullRefresh, queueCall.Options.MetadataRefreshMode);
                Assert.AreEqual(MetadataRefreshMode.FullRefresh, queueCall.Options.ImageRefreshMode);
                Assert.IsTrue(queueCall.Options.ReplaceAllMetadata);
                Assert.IsFalse(queueCall.Options.ReplaceAllImages);
                Assert.IsFalse(queueCall.Options.IsAutomated);
            }
        }

        private static bool IsSeriesQuery(InternalItemsQuery query)
        {
            return HasSingleIncludeItemType(query, BaseItemKind.Series)
                && query.IsVirtualItem == false
                && query.IsMissing == false
                && query.Recursive
                && query.HasTmdbId == true;
        }

        private static bool HasSingleIncludeItemType(InternalItemsQuery query, BaseItemKind itemType)
        {
            return query.IncludeItemTypes.Length == 1
                && query.IncludeItemTypes[0] == itemType;
        }

        private static void RecreatePluginInstance()
        {
            Directory.CreateDirectory(PluginsPath);
            Directory.CreateDirectory(PluginConfigurationsPath);

            var appHost = new Mock<IServerApplicationHost>();
            appHost.Setup(x => x.GetLocalApiUrl(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>())).Returns("http://127.0.0.1:8096");
            var applicationPaths = new Mock<IApplicationPaths>();
            applicationPaths.SetupGet(x => x.PluginsPath).Returns(PluginsPath);
            applicationPaths.SetupGet(x => x.PluginConfigurationsPath).Returns(PluginConfigurationsPath);

            _ = new MetaSharkPlugin(appHost.Object, applicationPaths.Object, new RecordingXmlSerializer());
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

            Assert.Fail("Could not initialize MetaSharkPlugin configuration for tests.");
        }

        private sealed record QueueRefreshCall(Guid ItemId, MetadataRefreshOptions Options, RefreshPriority Priority);

        private sealed record ServiceHarness(EpisodeGroupMappingConfigurationRefreshService Service, IReadOnlyCollection<QueueRefreshCall> QueueCalls);

        private sealed class RecordingXmlSerializer : IXmlSerializer
        {
            public object DeserializeFromStream(Type type, Stream stream)
            {
                throw new NotSupportedException();
            }

            public void SerializeToStream(object obj, Stream stream)
            {
                throw new NotSupportedException();
            }

            public void SerializeToFile(object obj, string file)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, string.Empty);
            }

            public object DeserializeFromFile(Type type, string file)
            {
                throw new NotSupportedException();
            }

            public object DeserializeFromBytes(Type type, byte[] buffer)
            {
                throw new NotSupportedException();
            }
        }
    }
}
