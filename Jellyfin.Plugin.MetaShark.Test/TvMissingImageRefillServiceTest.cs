using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaShark.Core;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Controller.BaseItemManager;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Moq;
using Jellyfin.Plugin.MetaShark.Test.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class TvMissingImageRefillServiceTest
    {
        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole());

        [TestMethod]
        public void QueueMissingImages_FullScan_QueuesSeriesWhenPrimaryIsMissingAndImageFetcherEnabled()
        {
            var series = new Series { Id = Guid.NewGuid(), Name = "Series A", Path = "/library/tv/series-a" };
            var seriesWithImages = new Series { Id = Guid.NewGuid(), Name = "Series B", Path = "/library/tv/series-b" };
            seriesWithImages.SetImagePath(ImageType.Primary, "https://example.com/images/series-b-primary.jpg");
            seriesWithImages.SetImagePath(ImageType.Backdrop, "https://example.com/images/series-b-backdrop.jpg");
            seriesWithImages.SetImagePath(ImageType.Logo, "https://example.com/images/series-b-logo.png");

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { series, seriesWithImages });

            var providerManagerStub = new Mock<IProviderManager>();

            var baseItemManagerStub = new Mock<IBaseItemManager>();
            baseItemManagerStub
                .Setup(x => x.IsImageFetcherEnabled(series, It.IsAny<TypeOptions>(), MetaSharkPlugin.PluginName))
                .Returns(true);
            baseItemManagerStub
                .Setup(x => x.IsImageFetcherEnabled(seriesWithImages, It.IsAny<TypeOptions>(), MetaSharkPlugin.PluginName))
                .Returns(true);

            var service = CreateServiceWithLogger(
                libraryManagerStub.Object,
                providerManagerStub.Object,
                baseItemManagerStub.Object,
                CreateResolver((series, true), (seriesWithImages, true)),
                out var loggerStub);

            var summary = service.QueueMissingImagesForFullLibraryScan(CancellationToken.None);

            providerManagerStub.Verify(
                x => x.QueueRefresh(
                    series.Id,
                    It.Is<MetadataRefreshOptions>(opt =>
                        opt.MetadataRefreshMode == MetadataRefreshMode.FullRefresh &&
                        opt.ImageRefreshMode == MetadataRefreshMode.FullRefresh &&
                        !opt.ReplaceAllImages &&
                        !opt.ReplaceAllMetadata),
                    RefreshPriority.Normal),
                Times.Once);

            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Debug,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["Name"] = "Series A",
                    ["Id"] = series.Id,
                    ["MissingImages"] = "Primary,Backdrop,Logo",
                },
                originalFormatContains: "[MetaShark] 已排队电视缺图回填",
                messageContains: ["[MetaShark] 已排队电视缺图回填", "itemId=", "missingImages=Primary,Backdrop,Logo"]);

            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Debug,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["Name"] = "Series B",
                    ["Id"] = seriesWithImages.Id,
                },
                originalFormatContains: "[MetaShark] 跳过电视缺图回填",
                messageContains: ["[MetaShark] 跳过电视缺图回填", "reason=NoMissingImages", "itemId="]);

            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Information,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["CandidateCount"] = 2,
                    ["QueuedCount"] = 1,
                    ["SkippedCount"] = 1,
                    ["SkippedReasons"] = "NoMissingImages=1",
                },
                originalFormatContains: "[MetaShark] 电视缺图回填扫描完成",
                messageContains: ["candidateCount=2", "queuedCount=1", "skippedCount=1", "skippedReasons=NoMissingImages=1"]);

            Assert.IsFalse(
                loggerStub.Invocations.Any(invocation =>
                    string.Equals(invocation.Method.Name, nameof(ILogger.Log), StringComparison.Ordinal) &&
                    invocation.Arguments.Count == 5 &&
                    invocation.Arguments[0] is LogLevel level &&
                    level == LogLevel.Information &&
                    invocation.Arguments[2]?.ToString()?.Contains("找到", StringComparison.Ordinal) == true),
                "旧的信息级文案仍然存在。");

            Assert.AreEqual(2, summary.CandidateCount);
            Assert.AreEqual(1, summary.QueuedCount);
            Assert.AreEqual(1, summary.SkippedCount);
            Assert.AreEqual("NoMissingImages=1", summary.SkippedReasons);
        }

        [TestMethod]
        public void QueueMissingImages_FullScan_DoesNotQueueWhenSeriesAlreadyHasAllSupportedImages()
        {
            var series = new Series { Id = Guid.NewGuid(), Name = "Series A", Path = "/library/tv/series-a" };
            series.SetImagePath(ImageType.Primary, "https://example.com/images/series-a-primary.jpg");
            series.SetImagePath(ImageType.Backdrop, "https://example.com/images/series-a-backdrop.jpg");
            series.SetImagePath(ImageType.Logo, "https://example.com/images/series-a-logo.png");

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { series });

            var providerManagerStub = new Mock<IProviderManager>();

            var baseItemManagerStub = new Mock<IBaseItemManager>();
            baseItemManagerStub
                .Setup(x => x.IsImageFetcherEnabled(series, It.IsAny<TypeOptions>(), MetaSharkPlugin.PluginName))
                .Returns(true);

            var service = CreateServiceWithLogger(
                libraryManagerStub.Object,
                providerManagerStub.Object,
                baseItemManagerStub.Object,
                CreateResolver((series, true)),
                out var loggerStub);

            var summary = service.QueueMissingImagesForFullLibraryScan(CancellationToken.None);

            providerManagerStub.Verify(
                x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()),
                Times.Never);

            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Debug,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["Name"] = "Series A",
                    ["Id"] = series.Id,
                },
                originalFormatContains: "[MetaShark] 跳过电视缺图回填",
                messageContains: ["[MetaShark] 跳过电视缺图回填", "reason=NoMissingImages", "itemId="]);

            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Information,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["CandidateCount"] = 1,
                    ["QueuedCount"] = 0,
                    ["SkippedCount"] = 1,
                    ["SkippedReasons"] = "NoMissingImages=1",
                },
                originalFormatContains: "[MetaShark] 电视缺图回填扫描完成",
                messageContains: ["candidateCount=1", "queuedCount=0", "skippedCount=1", "skippedReasons=NoMissingImages=1"]);

            Assert.IsFalse(
                loggerStub.Invocations.Any(invocation =>
                    string.Equals(invocation.Method.Name, nameof(ILogger.Log), StringComparison.Ordinal) &&
                    invocation.Arguments.Count == 5 &&
                    invocation.Arguments[0] is LogLevel level &&
                    level == LogLevel.Information &&
                    invocation.Arguments[2]?.ToString()?.Contains("找到", StringComparison.Ordinal) == true),
                "旧的信息级文案仍然存在。");

            Assert.AreEqual(1, summary.CandidateCount);
            Assert.AreEqual(0, summary.QueuedCount);
            Assert.AreEqual(1, summary.SkippedCount);
            Assert.AreEqual("NoMissingImages=1", summary.SkippedReasons);
        }

        [TestMethod]
        public void QueueMissingImages_FullScan_DoesNotQueueWhenImageFetcherDisabledForLibrary()
        {
            var series = new Series { Id = Guid.NewGuid(), Path = "/library/tv/series-a" };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { series });

            var providerManagerStub = new Mock<IProviderManager>();

            var baseItemManagerStub = new Mock<IBaseItemManager>();
            baseItemManagerStub
                .Setup(x => x.IsImageFetcherEnabled(series, It.IsAny<TypeOptions>(), MetaSharkPlugin.PluginName))
                .Returns(false);

            var service = CreateService(
                libraryManagerStub.Object,
                providerManagerStub.Object,
                baseItemManagerStub.Object,
                CreateResolver((series, false)));

            var summary = service.QueueMissingImagesForFullLibraryScan(CancellationToken.None);

            providerManagerStub.Verify(
                x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()),
                Times.Never);
            Assert.AreEqual(0, summary.QueuedCount);
            Assert.AreEqual(1, summary.SkippedCount);
            Assert.AreEqual("CapabilityDisabledForResolvedLibrary=1", summary.SkippedReasons);
        }

        [TestMethod]
        public void QueueMissingImages_FullScan_SkipsNonTvItems()
        {
            var movie = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Path = "/library/movies/movie-a.mkv" };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { movie });

            var providerManagerStub = new Mock<IProviderManager>();

            var baseItemManagerStub = new Mock<IBaseItemManager>();

            var service = CreateService(
                libraryManagerStub.Object,
                providerManagerStub.Object,
                baseItemManagerStub.Object);

            service.QueueMissingImagesForFullLibraryScan(CancellationToken.None);

            providerManagerStub.Verify(
                x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()),
                Times.Never);
        }

        [TestMethod]
        public void QueueMissingImages_FullScan_QueuesSeasonAndEpisodeWhenPrimaryMissing()
        {
            var season = new Season { Id = Guid.NewGuid(), Path = "/library/tv/series-a/Season 01" };
            var episode = new Episode { Id = Guid.NewGuid(), Path = "/library/tv/series-a/Season 01/episode-01.mkv" };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { season, episode });

            var providerManagerStub = new Mock<IProviderManager>();

            var baseItemManagerStub = new Mock<IBaseItemManager>();
            baseItemManagerStub
                .Setup(x => x.IsImageFetcherEnabled(season, It.IsAny<TypeOptions>(), MetaSharkPlugin.PluginName))
                .Returns(true);
            baseItemManagerStub
                .Setup(x => x.IsImageFetcherEnabled(episode, It.IsAny<TypeOptions>(), MetaSharkPlugin.PluginName))
                .Returns(true);

            var service = CreateService(
                libraryManagerStub.Object,
                providerManagerStub.Object,
                baseItemManagerStub.Object,
                CreateResolver((season, true), (episode, true)));

            service.QueueMissingImagesForFullLibraryScan(CancellationToken.None);

            providerManagerStub.Verify(
                x => x.QueueRefresh(season.Id, It.IsAny<MetadataRefreshOptions>(), RefreshPriority.Normal),
                Times.Once);
            providerManagerStub.Verify(
                x => x.QueueRefresh(episode.Id, It.IsAny<MetadataRefreshOptions>(), RefreshPriority.Normal),
                Times.Once);
        }

        [TestMethod]
        public void QueueMissingImages_ItemUpdate_QueuesWhenMetadataImportAndSeriesMissingImages()
        {
            var series = new Series { Id = Guid.NewGuid(), Path = "/library/tv/series-a" };

            var providerManagerStub = new Mock<IProviderManager>();
            var baseItemManagerStub = new Mock<IBaseItemManager>();
            baseItemManagerStub
                .Setup(x => x.IsImageFetcherEnabled(series, It.IsAny<TypeOptions>(), MetaSharkPlugin.PluginName))
                .Returns(true);

            var service = CreateService(
                new Mock<ILibraryManager>().Object,
                providerManagerStub.Object,
                baseItemManagerStub.Object,
                CreateResolver((series, true)));

            service.QueueMissingImagesForUpdatedItem(new ItemChangeEventArgs
            {
                Item = series,
                UpdateReason = ItemUpdateType.MetadataImport,
            },
            CancellationToken.None);

            providerManagerStub.Verify(
                x => x.QueueRefresh(series.Id, It.IsAny<MetadataRefreshOptions>(), RefreshPriority.Normal),
                Times.Once);
        }

        [TestMethod]
        public void QueueMissingImages_ItemUpdate_DoesNotQueueForImageUpdateReason()
        {
            var series = new Series { Id = Guid.NewGuid(), Name = "Series A", Path = "/library/tv/series-a" };

            var providerManagerStub = new Mock<IProviderManager>();
            var baseItemManagerStub = new Mock<IBaseItemManager>();
            baseItemManagerStub
                .Setup(x => x.IsImageFetcherEnabled(series, It.IsAny<TypeOptions>(), MetaSharkPlugin.PluginName))
                .Returns(true);

            var service = CreateServiceWithLogger(new Mock<ILibraryManager>().Object, providerManagerStub.Object, baseItemManagerStub.Object, null, out var loggerStub);

            service.QueueMissingImagesForUpdatedItem(new ItemChangeEventArgs
            {
                Item = series,
                UpdateReason = ItemUpdateType.ImageUpdate,
            },
            CancellationToken.None);

            providerManagerStub.Verify(
                x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()),
                Times.Never);

            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Debug,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["Name"] = "Series A",
                    ["Id"] = series.Id,
                    ["UpdateReason"] = ItemUpdateType.ImageUpdate,
                },
                originalFormatContains: "[MetaShark] 跳过电视缺图回填",
                messageContains: ["[MetaShark] 跳过电视缺图回填", "reason=UpdateReasonRejected", "updateReason=ImageUpdate"]);
        }

        [TestMethod]
        public void QueueMissingImages_ItemUpdate_DoesNotQueueForNonTvItem()
        {
            var movie = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Path = "/library/movies/movie-a.mkv" };

            var providerManagerStub = new Mock<IProviderManager>();
            var baseItemManagerStub = new Mock<IBaseItemManager>();

            var service = CreateService(new Mock<ILibraryManager>().Object, providerManagerStub.Object, baseItemManagerStub.Object);

            service.QueueMissingImagesForUpdatedItem(new ItemChangeEventArgs
            {
                Item = movie,
                UpdateReason = ItemUpdateType.MetadataImport,
            },
            CancellationToken.None);

            providerManagerStub.Verify(
                x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()),
                Times.Never);
        }

        [TestMethod]
        public void QueueMissingImages_ItemUpdate_DoesNotQueueWhenSeriesHasNoId()
        {
            var series = new Series { Id = Guid.Empty, Path = "/library/tv/series-a" };

            var providerManagerStub = new Mock<IProviderManager>();
            var baseItemManagerStub = new Mock<IBaseItemManager>();
            baseItemManagerStub
                .Setup(x => x.IsImageFetcherEnabled(series, It.IsAny<TypeOptions>(), MetaSharkPlugin.PluginName))
                .Returns(true);

            var service = CreateService(new Mock<ILibraryManager>().Object, providerManagerStub.Object, baseItemManagerStub.Object);

            service.QueueMissingImagesForUpdatedItem(new ItemChangeEventArgs
            {
                Item = series,
                UpdateReason = ItemUpdateType.MetadataImport,
            },
            CancellationToken.None);

            providerManagerStub.Verify(
                x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()),
                Times.Never);
        }

        private TvMissingImageRefillService CreateService(
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IBaseItemManager baseItemManager,
            MetaSharkOrdinaryItemLibraryCapabilityResolver? ordinaryItemLibraryCapabilityResolver = null)
        {
            return new TvMissingImageRefillService(
                this.loggerFactory,
                libraryManager,
                providerManager,
                baseItemManager,
                new Mock<IFileSystem>().Object,
                ordinaryItemLibraryCapabilityResolver: ordinaryItemLibraryCapabilityResolver);
        }

        private static TvMissingImageRefillService CreateServiceWithLogger(
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IBaseItemManager baseItemManager,
            MetaSharkOrdinaryItemLibraryCapabilityResolver? ordinaryItemLibraryCapabilityResolver,
            out Mock<ILogger> loggerStub)
        {
            loggerStub = new Mock<ILogger>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            var loggerFactoryStub = new Mock<ILoggerFactory>();
            loggerFactoryStub.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(loggerStub.Object);

            return new TvMissingImageRefillService(
                loggerFactoryStub.Object,
                libraryManager,
                providerManager,
                baseItemManager,
                new Mock<IFileSystem>().Object,
                ordinaryItemLibraryCapabilityResolver: ordinaryItemLibraryCapabilityResolver);
        }

        private static MetaSharkOrdinaryItemLibraryCapabilityResolver CreateResolver(params (BaseItem Item, bool ImageAllowed)[] entries)
        {
            var libraryOptionsByItem = entries.ToDictionary(
                entry => entry.Item,
                entry => CreateLibraryOptions(ResolveItemType(entry.Item), entry.ImageAllowed));

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetLibraryOptions(It.IsAny<BaseItem>()))
                .Returns((BaseItem item) => libraryOptionsByItem.TryGetValue(item, out var libraryOptions) ? libraryOptions : null!);

            return new MetaSharkOrdinaryItemLibraryCapabilityResolver(libraryManagerStub.Object);
        }

        private static LibraryOptions CreateLibraryOptions(string itemType, bool imageAllowed)
        {
            return new LibraryOptions
            {
                TypeOptions = new[]
                {
                    new TypeOptions
                    {
                        Type = itemType,
                        MetadataFetchers = Array.Empty<string>(),
                        ImageFetchers = imageAllowed ? new[] { MetaSharkPlugin.PluginName } : Array.Empty<string>(),
                    },
                },
            };
        }

        private static string ResolveItemType(BaseItem item)
        {
            return item switch
            {
                Series => nameof(Series),
                Season => nameof(Season),
                Episode => nameof(Episode),
                _ => throw new ArgumentOutOfRangeException(nameof(item), item.GetType().FullName, "Unsupported TV item type."),
            };
        }

    }
}
