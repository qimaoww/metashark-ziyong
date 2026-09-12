using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaShark.EpisodeGroupMapping;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class EpisodeGroupRefreshQueueSelectorTest
    {
        [TestMethod]
        public void SelectQueueableRefreshPlans_AppendsLinkedAlternateVersionEpisodes()
        {
            var series = new Series
            {
                Id = Guid.NewGuid(),
                Name = "Series A",
                Path = "/library/tv/series-a",
            };
            series.SetProviderId(MetadataProvider.Tmdb, "65942");

            var episode = new Episode
            {
                Id = Guid.NewGuid(),
                Name = "Episode 1",
                Path = "/library/tv/series-a/Season 01/episode-01.mkv",
            };
            var linkedVersion = new Episode
            {
                Id = Guid.NewGuid(),
                Name = "Episode 1 (另一版本)",
                Path = "/library/tv-alt/series-a/Season 01/episode-01.mkv",
            };

            var capturedQueries = new List<InternalItemsQuery>();
            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Callback<InternalItemsQuery>(query => capturedQueries.Add(query))
                .Returns((InternalItemsQuery query) =>
                {
                    if (query.IncludeItemTypes?.Contains(BaseItemKind.Series) == true)
                    {
                        return new List<BaseItem> { series };
                    }

                    if (query.IncludeItemTypes?.Contains(BaseItemKind.Season) == true)
                    {
                        return new List<BaseItem>();
                    }

                    if (query.IncludeItemTypes?.Contains(BaseItemKind.Episode) == true)
                    {
                        return new List<BaseItem> { episode };
                    }

                    return new List<BaseItem>();
                });
            libraryManagerStub
                .Setup(x => x.GetItemById(linkedVersion.Id))
                .Returns(linkedVersion);

            var fileSystemStub = new Mock<IFileSystem>();
            fileSystemStub.Setup(x => x.DirectoryExists(It.IsAny<string>())).Returns(true);

            var linkedChildrenStub = new Mock<ILinkedChildrenService>();
            linkedChildrenStub
                .Setup(x => x.GetItemIdsWithAlternateVersions(It.IsAny<IReadOnlyList<Guid>>()))
                .Returns(new HashSet<Guid> { episode.Id });
            linkedChildrenStub
                .Setup(x => x.GetLinkedChildrenIds(episode.Id, (int)LinkedChildType.LinkedAlternateVersion))
                .Returns(new[] { linkedVersion.Id });
            linkedChildrenStub
                .Setup(x => x.GetLinkedChildrenIds(episode.Id, (int)LinkedChildType.LocalAlternateVersion))
                .Returns(Array.Empty<Guid>());

            var plans = EpisodeGroupRefreshQueueSelector.SelectQueueableRefreshPlans(
                libraryManagerStub.Object,
                fileSystemStub.Object,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "65942" },
                item => item.ProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var tmdbId) ? tmdbId : null,
                linkedChildrenStub.Object);

            Assert.AreEqual(1, plans.Count);
            CollectionAssert.AreEquivalent(
                new[] { episode.Id, linkedVersion.Id },
                plans[0].Targets.Select(target => target.Item.Id).ToArray());
            Assert.IsTrue(
                plans[0].Targets.All(target => target.Mode == EpisodeGroupRefreshQueueSelector.EpisodeGroupRefreshQueueMode.MetadataRefresh),
                "季与分集目标都应使用元数据刷新模式。");

            Assert.IsTrue(
                capturedQueries.Any(query => query.IncludeItemTypes?.Contains(BaseItemKind.Episode) == true && query.IncludeOwnedItems),
                "分集查询必须包含 OwnerId 非空的多版本条目。");
            Assert.IsTrue(
                capturedQueries.Any(query => query.IncludeItemTypes?.Contains(BaseItemKind.Season) == true
                    && query.IsVirtualItem == false
                    && query.IsMissing == false),
                "虚拟季与缺失季不应进入刷新队列。");
        }

        [TestMethod]
        public void SelectQueueableRefreshPlans_WithoutLinkedChildrenService_StillQueuesOwnEpisodes()
        {
            var series = new Series
            {
                Id = Guid.NewGuid(),
                Name = "Series B",
                Path = "/library/tv/series-b",
            };
            series.SetProviderId(MetadataProvider.Tmdb, "1001");

            var episode = new Episode
            {
                Id = Guid.NewGuid(),
                Name = "Episode 1",
                Path = "/library/tv/series-b/Season 01/episode-01.mkv",
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns((InternalItemsQuery query) =>
                {
                    if (query.IncludeItemTypes?.Contains(BaseItemKind.Series) == true)
                    {
                        return new List<BaseItem> { series };
                    }

                    return query.IncludeItemTypes?.Contains(BaseItemKind.Episode) == true
                        ? new List<BaseItem> { episode }
                        : new List<BaseItem>();
                });

            var fileSystemStub = new Mock<IFileSystem>();
            fileSystemStub.Setup(x => x.DirectoryExists(It.IsAny<string>())).Returns(true);

            var plans = EpisodeGroupRefreshQueueSelector.SelectQueueableRefreshPlans(
                libraryManagerStub.Object,
                fileSystemStub.Object,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "1001" },
                item => item.ProviderIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var tmdbId) ? tmdbId : null);

            Assert.AreEqual(1, plans.Count);
            Assert.AreEqual(1, plans[0].Targets.Count);
            Assert.AreEqual(episode.Id, plans[0].Targets[0].Item.Id);
        }
    }
}
