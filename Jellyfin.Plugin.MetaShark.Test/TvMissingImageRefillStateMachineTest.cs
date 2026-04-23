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
using System;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class TvMissingImageRefillStateMachineTest
    {
        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder.AddSimpleConsole());

        [TestMethod]
        public void TvMissingImageRefillService_RequiresRetryStateStoreContract()
        {
            Assert.IsTrue(
                HasConstructorParameterFragment(typeof(TvMissingImageRefillService), "Store"),
                "TvMissingImageRefillService needs a retry-state store abstraction before hard-miss, cooldown, and fingerprint gating can be implemented.");
        }

        [TestMethod]
        public void QueueMissingImages_ItemUpdate_DoesNotQueueWhenEpisodeIsHardMiss()
        {
            var service = CreateService(out var providerManagerStub, out var baseItemManagerStub);
            baseItemManagerStub
                .Setup(x => x.IsImageFetcherEnabled(It.IsAny<BaseItem>(), It.IsAny<TypeOptions>(), MetaSharkPlugin.PluginName))
                .Returns(true);

            var episode = new Episode
            {
                Id = Guid.NewGuid(),
                Name = "Episode 0",
                Path = "/library/tv/series-a/Season 01/episode-00.mkv",
                ParentIndexNumber = 1,
                IndexNumber = 0,
            };

            service.QueueMissingImagesForUpdatedItem(
                new ItemChangeEventArgs
                {
                    Item = episode,
                    UpdateReason = ItemUpdateType.MetadataImport,
                },
                CancellationToken.None);

            providerManagerStub.Verify(
                x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()),
                Times.Never);
        }

        [TestMethod]
        public void QueueMissingImages_ItemUpdate_DoesNotQueueAgainWhileCooldownActive()
        {
            var service = CreateService(out var providerManagerStub, out var baseItemManagerStub);
            baseItemManagerStub
                .Setup(x => x.IsImageFetcherEnabled(It.IsAny<BaseItem>(), It.IsAny<TypeOptions>(), MetaSharkPlugin.PluginName))
                .Returns(true);

            var series = new Series
            {
                Id = Guid.NewGuid(),
                Name = "Series A",
                Path = "/library/tv/series-a",
            };

            var itemUpdate = new ItemChangeEventArgs
            {
                Item = series,
                UpdateReason = ItemUpdateType.MetadataImport,
            };

            service.QueueMissingImagesForUpdatedItem(itemUpdate, CancellationToken.None);
            service.QueueMissingImagesForUpdatedItem(itemUpdate, CancellationToken.None);

            providerManagerStub.Verify(
                x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()),
                Times.Once);
        }

        [TestMethod]
        public void QueueMissingImages_ItemUpdate_QueuesAfterFingerprintChange()
        {
            var service = CreateService(out var providerManagerStub, out var baseItemManagerStub);
            baseItemManagerStub
                .Setup(x => x.IsImageFetcherEnabled(It.IsAny<BaseItem>(), It.IsAny<TypeOptions>(), MetaSharkPlugin.PluginName))
                .Returns(true);

            var itemId = Guid.NewGuid();
            var firstEpisode = new Episode
            {
                Id = itemId,
                Name = "Episode 0",
                Path = "/library/tv/series-a/Season 01/episode-00.mkv",
                ParentIndexNumber = 1,
                IndexNumber = 0,
            };
            var secondEpisode = new Episode
            {
                Id = itemId,
                Name = "Episode 1",
                Path = "/library/tv/series-a/Season 01/episode-01.mkv",
                ParentIndexNumber = 1,
                IndexNumber = 1,
            };

            service.QueueMissingImagesForUpdatedItem(
                new ItemChangeEventArgs
                {
                    Item = firstEpisode,
                    UpdateReason = ItemUpdateType.MetadataImport,
                },
                CancellationToken.None);

            service.QueueMissingImagesForUpdatedItem(
                new ItemChangeEventArgs
                {
                    Item = secondEpisode,
                    UpdateReason = ItemUpdateType.MetadataImport,
                },
                CancellationToken.None);

            providerManagerStub.Verify(
                x => x.QueueRefresh(It.IsAny<Guid>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<RefreshPriority>()),
                Times.Once);
        }

        private TvMissingImageRefillService CreateService(
            out Mock<IProviderManager> providerManagerStub,
            out Mock<IBaseItemManager> baseItemManagerStub)
        {
            var libraryManagerStub = new Mock<ILibraryManager>();
            providerManagerStub = new Mock<IProviderManager>();
            baseItemManagerStub = new Mock<IBaseItemManager>();

            return new TvMissingImageRefillService(
                this.loggerFactory,
                libraryManagerStub.Object,
                providerManagerStub.Object,
                baseItemManagerStub.Object,
                new Mock<IFileSystem>().Object,
                ordinaryItemLibraryCapabilityResolver: CreateResolver());
        }

        private static MetaSharkOrdinaryItemLibraryCapabilityResolver CreateResolver()
        {
            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetLibraryOptions(It.IsAny<BaseItem>()))
                .Returns((BaseItem item) => item switch
                {
                    Series => CreateLibraryOptions(nameof(Series)),
                    Season => CreateLibraryOptions(nameof(Season)),
                    Episode => CreateLibraryOptions(nameof(Episode)),
                    _ => null!,
                });

            return new MetaSharkOrdinaryItemLibraryCapabilityResolver(libraryManagerStub.Object);
        }

        private static LibraryOptions CreateLibraryOptions(string itemType)
        {
            return new LibraryOptions
            {
                TypeOptions = new[]
                {
                    new TypeOptions
                    {
                        Type = itemType,
                        MetadataFetchers = Array.Empty<string>(),
                        ImageFetchers = new[] { MetaSharkPlugin.PluginName },
                    },
                },
            };
        }

        private static bool HasConstructorParameterFragment(Type type, string fragment)
        {
            return type.GetConstructors()
                .Any(ctor => ctor.GetParameters()
                    .Any(parameter =>
                        (parameter.ParameterType.Name ?? string.Empty).Contains(fragment, StringComparison.OrdinalIgnoreCase)
                        || (parameter.Name ?? string.Empty).Contains(fragment, StringComparison.OrdinalIgnoreCase)));
        }
    }
}
