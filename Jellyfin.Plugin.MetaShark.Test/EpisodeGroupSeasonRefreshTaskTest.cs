using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.ScheduledTasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [TestCategory("Stable")]
    public class EpisodeGroupSeasonRefreshTaskTest
    {
        [TestMethod]
        public void MetadataProperties_ShouldMatchContract()
        {
            var task = CreateTask();

            Assert.AreEqual("MetaSharkRefreshEpisodeGroupSeasons", task.Key);
            Assert.AreEqual("刷新剧集组映射季", task.Name);
            Assert.AreEqual("对已配置 TMDb 剧集组映射的剧集逐季扫描新的和有修改的文件", task.Description);
            Assert.AreEqual(MetaSharkPlugin.PluginName, task.Category);
        }

        [TestMethod]
        public void GetDefaultTriggers_ShouldReturnNoDefaultTriggers()
        {
            var task = CreateTask();

            Assert.AreEqual(0, task.GetDefaultTriggers().Count());
        }

        [TestMethod]
        public async Task ExecuteAsync_WhenNoEffectiveMappingExists_DoesNotQueryLibraryAndReportsComplete()
        {
            var libraryManagerStub = new Mock<ILibraryManager>(MockBehavior.Strict);
            var providerManagerStub = new Mock<IProviderManager>(MockBehavior.Strict);
            var progress = new ProgressRecorder();
            var task = CreateTask(
                libraryManager: libraryManagerStub.Object,
                providerManager: providerManagerStub.Object,
                configuration: new PluginConfiguration());

            await task.ExecuteAsync(progress, CancellationToken.None).ConfigureAwait(false);

            CollectionAssert.AreEqual(new[] { 100d }, progress.Values.ToArray());
            libraryManagerStub.Verify(x => x.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
            providerManagerStub.Verify(
                x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()),
                Times.Never);
        }

        [TestMethod]
        public async Task ExecuteAsync_WhenMappedSeriesHasSeasons_QueuesSeasonScanRefreshes()
        {
            var manualMappedSeries = CreateSeries(Guid.NewGuid(), "Manual mapped", "65942");
            var llmMappedSeries = CreateSeries(Guid.NewGuid(), "LLM mapped", "70000");
            var ignoredSeries = CreateSeries(Guid.NewGuid(), "Ignored", "80000");
            var manualFirstSeason = CreateSeason(Guid.NewGuid(), "Manual Season 1", manualMappedSeries.Id);
            var manualSecondSeason = CreateSeason(Guid.NewGuid(), "Manual Season 2", manualMappedSeries.Id);
            var llmFirstSeason = CreateSeason(Guid.NewGuid(), "LLM Season 1", llmMappedSeries.Id);
            var seasonsBySeriesId = new Dictionary<Guid, IReadOnlyList<BaseItem>>
            {
                [manualMappedSeries.Id] = new BaseItem[] { manualFirstSeason, manualSecondSeason },
                [llmMappedSeries.Id] = new BaseItem[] { llmFirstSeason },
            };
            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.Is<InternalItemsQuery>(query => IsSeriesQuery(query))))
                .Returns(new List<BaseItem> { manualMappedSeries, llmMappedSeries, ignoredSeries });
            libraryManagerStub
                .Setup(x => x.GetItemList(It.Is<InternalItemsQuery>(query => HasSingleIncludeItemType(query, BaseItemKind.Season))))
                .Returns<InternalItemsQuery>(query =>
                    query.AncestorIds.Length == 1
                    && seasonsBySeriesId.TryGetValue(query.AncestorIds[0], out var seasons)
                        ? seasons.ToList()
                        : new List<BaseItem>());

            var queueCalls = new List<QueueRefreshCall>();
            var providerManagerStub = new Mock<IProviderManager>();
            providerManagerStub
                .Setup(x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()))
                .Callback<Guid, MetadataRefreshOptions, RefreshPriority>((itemId, options, priority) => queueCalls.Add(new QueueRefreshCall(itemId, options, priority)));

            var progress = new ProgressRecorder();
            var task = CreateTask(
                libraryManager: libraryManagerStub.Object,
                providerManager: providerManagerStub.Object,
                configuration: new PluginConfiguration
                {
                    TmdbEpisodeGroupMap = "65942=manual-group",
                    LlmTmdbEpisodeGroupMap = "70000=llm-group",
                });

            await task.ExecuteAsync(progress, CancellationToken.None).ConfigureAwait(false);

            CollectionAssert.AreEqual(new[] { 50d, 100d }, progress.Values.ToArray());
            CollectionAssert.AreEqual(
                new[] { manualFirstSeason.Id, manualSecondSeason.Id, llmFirstSeason.Id },
                queueCalls.Select(call => call.ItemId).ToArray());
            foreach (var queueCall in queueCalls)
            {
                Assert.AreEqual(RefreshPriority.High, queueCall.Priority);
                Assert.AreEqual(MetadataRefreshMode.Default, queueCall.Options.MetadataRefreshMode);
                Assert.AreEqual(MetadataRefreshMode.Default, queueCall.Options.ImageRefreshMode);
                Assert.IsFalse(queueCall.Options.ReplaceAllMetadata);
                Assert.IsFalse(queueCall.Options.ReplaceAllImages);
                Assert.IsFalse(queueCall.Options.IsAutomated);
            }

            libraryManagerStub.Verify(
                x => x.GetItemList(It.Is<InternalItemsQuery>(query => IsSeasonQueryForAncestor(query, manualMappedSeries.Id))),
                Times.Once);
            libraryManagerStub.Verify(
                x => x.GetItemList(It.Is<InternalItemsQuery>(query => IsSeasonQueryForAncestor(query, llmMappedSeries.Id))),
                Times.Once);
            Assert.IsFalse(queueCalls.Any(call => call.ItemId == ignoredSeries.Id));
        }

        private static EpisodeGroupSeasonRefreshTask CreateTask(
            ILibraryManager? libraryManager = null,
            IProviderManager? providerManager = null,
            PluginConfiguration? configuration = null)
        {
            return new EpisodeGroupSeasonRefreshTask(
                Mock.Of<ILogger<EpisodeGroupSeasonRefreshTask>>(),
                libraryManager ?? Mock.Of<ILibraryManager>(),
                providerManager ?? Mock.Of<IProviderManager>(),
                Mock.Of<IFileSystem>(),
                () => configuration ?? new PluginConfiguration());
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

        private static bool IsSeriesQuery(InternalItemsQuery query)
        {
            return HasSingleIncludeItemType(query, BaseItemKind.Series)
                && query.IsVirtualItem == false
                && query.IsMissing == false
                && query.Recursive
                && query.HasTmdbId == true;
        }

        private static bool IsSeasonQueryForAncestor(InternalItemsQuery query, Guid ancestorId)
        {
            return HasSingleIncludeItemType(query, BaseItemKind.Season)
                && query.IsVirtualItem == null
                && query.IsMissing == null
                && query.ParentId == Guid.Empty
                && query.AncestorIds.Length == 1
                && query.AncestorIds[0] == ancestorId;
        }

        private static bool HasSingleIncludeItemType(InternalItemsQuery query, BaseItemKind itemType)
        {
            return query.IncludeItemTypes.Length == 1
                && query.IncludeItemTypes[0] == itemType;
        }

        private sealed class ProgressRecorder : IProgress<double>
        {
            public List<double> Values { get; } = new();

            public void Report(double value)
            {
                this.Values.Add(value);
            }
        }

        private sealed record QueueRefreshCall(Guid ItemId, MetadataRefreshOptions Options, RefreshPriority Priority);
    }
}
