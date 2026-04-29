using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;
using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class TvImageRefillFingerprintTest
    {
        [TestMethod]
        public void Create_LeavesTmdbSegmentEmptyForEpisode_WhenOnlyLegacySeriesTmdbIdsExist()
        {
            var series = CreateSeries(new Dictionary<string, string>
            {
                [MetaSharkPlugin.ProviderId] = "Tmdb_65942",
                ["MetaSharkTmdbID"] = "65942",
            });
            var episode = CreateEpisode(series);
            var libraryManagerStub = CreateLibraryManager(series);

            WithLibraryManager(libraryManagerStub.Object, () =>
            {
                var fingerprint = TvImageRefillFingerprint.Create(episode);

                Assert.AreEqual("/library/tv/series-a/Season 01/episode-01.mkv||1|1", fingerprint);
            });
        }

        [TestMethod]
        public void Create_UsesStandardSeriesTmdbIdAndIgnoresLegacySeriesTmdbIdsForEpisode()
        {
            var series = CreateSeries(new Dictionary<string, string>
            {
                [MetadataProvider.Tmdb.ToString()] = "123",
                [MetaSharkPlugin.ProviderId] = "Tmdb_65942",
                ["MetaSharkTmdbID"] = "65942",
            });
            var episode = CreateEpisode(series);
            var libraryManagerStub = CreateLibraryManager(series);

            WithLibraryManager(libraryManagerStub.Object, () =>
            {
                var fingerprint = TvImageRefillFingerprint.Create(episode);

                Assert.AreEqual("/library/tv/series-a/Season 01/episode-01.mkv|123|1|1", fingerprint);
            });
        }

        private static Series CreateSeries(Dictionary<string, string> providerIds)
        {
            return new Series
            {
                Id = Guid.NewGuid(),
                Name = "Series A",
                Path = "/library/tv/series-a",
                ProviderIds = providerIds,
            };
        }

        private static Episode CreateEpisode(Series series)
        {
            return new Episode
            {
                Id = Guid.NewGuid(),
                Name = "Episode 1",
                Path = "/library/tv/series-a/Season 01/episode-01.mkv",
                ParentIndexNumber = 1,
                IndexNumber = 1,
                SeriesId = series.Id,
                SeriesName = series.Name ?? string.Empty,
            };
        }

        private static Mock<ILibraryManager> CreateLibraryManager(Series series)
        {
            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub.Setup(x => x.GetItemById(series.Id)).Returns((BaseItem)series);
            return libraryManagerStub;
        }

        private static void WithLibraryManager(ILibraryManager libraryManager, Action action)
        {
            var originalLibraryManager = BaseItem.LibraryManager;
            BaseItem.LibraryManager = libraryManager;

            try
            {
                action();
            }
            finally
            {
                BaseItem.LibraryManager = originalLibraryManager;
            }
        }
    }
}
