using Jellyfin.Data.Enums;
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

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns(new List<BaseItem> { series });

            var providerManagerStub = new Mock<IProviderManager>();

            var baseItemManagerStub = new Mock<IBaseItemManager>();
            baseItemManagerStub
                .Setup(x => x.IsImageFetcherEnabled(series, It.IsAny<TypeOptions>(), MetaSharkPlugin.PluginName))
                .Returns(true);

            var service = CreateServiceWithLogger(libraryManagerStub.Object, providerManagerStub.Object, baseItemManagerStub.Object, out var loggerStub);

            service.QueueMissingImagesForFullLibraryScan(CancellationToken.None);

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

            VerifyLoggedMessage(loggerStub, LogLevel.Debug, "Primary,Backdrop,Logo", Times.Once());
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

            var service = CreateServiceWithLogger(libraryManagerStub.Object, providerManagerStub.Object, baseItemManagerStub.Object, out var loggerStub);

            service.QueueMissingImagesForFullLibraryScan(CancellationToken.None);

            providerManagerStub.Verify(
                x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()),
                Times.Never);

            VerifyLoggedMessage(loggerStub, LogLevel.Debug, "no supported images are missing", Times.Once());
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

            var service = CreateService(libraryManagerStub.Object, providerManagerStub.Object, baseItemManagerStub.Object);

            service.QueueMissingImagesForFullLibraryScan(CancellationToken.None);

            providerManagerStub.Verify(
                x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()),
                Times.Never);
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

            var service = CreateService(libraryManagerStub.Object, providerManagerStub.Object, baseItemManagerStub.Object);

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

            var service = CreateService(libraryManagerStub.Object, providerManagerStub.Object, baseItemManagerStub.Object);

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

            var service = CreateService(new Mock<ILibraryManager>().Object, providerManagerStub.Object, baseItemManagerStub.Object);

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

            var service = CreateServiceWithLogger(new Mock<ILibraryManager>().Object, providerManagerStub.Object, baseItemManagerStub.Object, out var loggerStub);

            service.QueueMissingImagesForUpdatedItem(new ItemChangeEventArgs
            {
                Item = series,
                UpdateReason = ItemUpdateType.ImageUpdate,
            },
            CancellationToken.None);

            providerManagerStub.Verify(
                x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()),
                Times.Never);

            VerifyLoggedMessage(loggerStub, LogLevel.Debug, "ImageUpdate", Times.Once());
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
            IBaseItemManager baseItemManager)
        {
            return new TvMissingImageRefillService(
                this.loggerFactory,
                libraryManager,
                providerManager,
                baseItemManager,
                new Mock<IFileSystem>().Object);
        }

        private static TvMissingImageRefillService CreateServiceWithLogger(
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IBaseItemManager baseItemManager,
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
                new Mock<IFileSystem>().Object);
        }

        private static void VerifyLoggedMessage(Mock<ILogger> loggerStub, LogLevel level, string fragment, Times times)
        {
            loggerStub.Verify(
                x => x.Log(
                    level,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(fragment, StringComparison.Ordinal)),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                times);
        }
    }
}
