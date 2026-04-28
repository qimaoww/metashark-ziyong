using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class SeriesTmdbProviderIdMigrationServiceTest
    {
        [TestMethod]
        public async Task MigrateItemAsync_SeriesWithOfficialTmdbOnly_ShouldMoveToPrivateIdAndPersist()
        {
            var service = CreateService();
            var series = CreateSeries();
            series.SetProviderId(MetadataProvider.Tmdb, "123456");

            var migrated = await service.MigrateItemAsync(series, "Test", CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(migrated);
            Assert.IsNull(series.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("123456", series.GetProviderId(BaseProvider.MetaSharkTmdbProviderId));
            Assert.AreEqual("Tmdb_123456", series.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual(1, series.MetadataChangedCallCount);
            Assert.AreEqual(1, series.UpdateToRepositoryCallCount);
            Assert.AreEqual(ItemUpdateType.MetadataEdit, series.LastUpdateReason);
        }

        [TestMethod]
        public async Task MigrateItemAsync_SeriesWithPrivateAndDoubanIds_ShouldPreserveExistingMetaSharkIds()
        {
            var service = CreateService();
            var series = CreateSeries();
            series.SetProviderId(MetadataProvider.Tmdb, "123456");
            series.SetProviderId(BaseProvider.MetaSharkTmdbProviderId, "654321");
            series.SetProviderId(MetaSharkPlugin.ProviderId, "Douban_abc");

            var migrated = await service.MigrateItemAsync(series, "Test", CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(migrated);
            Assert.IsNull(series.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("654321", series.GetProviderId(BaseProvider.MetaSharkTmdbProviderId));
            Assert.AreEqual("Douban_abc", series.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual(1, series.MetadataChangedCallCount);
            Assert.AreEqual(1, series.UpdateToRepositoryCallCount);
        }

        [TestMethod]
        public async Task MigrateItemAsync_SeriesWithDoubanIdAndMissingMetaSharkId_ShouldNotForceTmdbMetaSource()
        {
            var service = CreateService();
            var series = CreateSeries();
            series.SetProviderId(MetadataProvider.Tmdb, "123456");
            series.SetProviderId(BaseProvider.DoubanProviderId, "douban-series");

            var migrated = await service.MigrateItemAsync(series, "Test", CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(migrated);
            Assert.IsNull(series.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("123456", series.GetProviderId(BaseProvider.MetaSharkTmdbProviderId));
            Assert.AreEqual("douban-series", series.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.IsNull(series.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual(1, series.UpdateToRepositoryCallCount);
        }

        [TestMethod]
        public async Task MigrateItemAsync_InvalidOfficialTmdbId_ShouldRemoveWithoutCopyingPrivateId()
        {
            var service = CreateService();
            var series = CreateSeries();
            series.SetProviderId(MetadataProvider.Tmdb, "abc");

            var migrated = await service.MigrateItemAsync(series, "Test", CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(migrated);
            Assert.IsNull(series.GetProviderId(MetadataProvider.Tmdb));
            Assert.IsNull(series.GetProviderId(BaseProvider.MetaSharkTmdbProviderId));
            Assert.IsNull(series.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual(1, series.UpdateToRepositoryCallCount);
        }

        [TestMethod]
        public async Task MigrateItemAsync_InvalidPrivateTmdbIdAndValidOfficialTmdbId_ShouldOverwritePrivateId()
        {
            var service = CreateService();
            var series = CreateSeries();
            series.SetProviderId(MetadataProvider.Tmdb, "123456");
            series.SetProviderId(BaseProvider.MetaSharkTmdbProviderId, "abc");

            var migrated = await service.MigrateItemAsync(series, "Test", CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(migrated);
            Assert.IsNull(series.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("123456", series.GetProviderId(BaseProvider.MetaSharkTmdbProviderId));
            Assert.AreEqual(1, series.UpdateToRepositoryCallCount);
        }

        [TestMethod]
        public async Task MigrateItemAsync_SeriesWithOnlyNfoTmdbResidue_ShouldCleanNfoWithoutPersistingItem()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"metashark-series-tmdb-nfo-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(tempRoot);
                var nfoPath = Path.Combine(tempRoot, "tvshow.nfo");
                File.WriteAllText(
                    nfoPath,
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<tvshow>\n  <title>Series A</title>\n  <tmdbid>123456</tmdbid>\n  <uniqueid type=\"tmdb\" default=\"true\">123456</uniqueid>\n  <uniqueid type=\"imdb\">tt1234567</uniqueid>\n  <id IMDb=\"tt1234567\" TMDB=\"123456\">tt1234567</id>\n  <url>https://www.themoviedb.org/tv/123456-series-a</url>\n</tvshow>\n");

                var service = CreateService();
                var series = CreateSeries();
                series.Path = tempRoot;
                series.SetProviderId(BaseProvider.MetaSharkTmdbProviderId, "123456");

                var migrated = await service.MigrateItemAsync(series, "Test", CancellationToken.None).ConfigureAwait(false);

                Assert.IsTrue(migrated);
                Assert.AreEqual(0, series.UpdateToRepositoryCallCount);
                var nfo = File.ReadAllText(nfoPath);
                StringAssert.Contains(nfo, "<title>Series A</title>");
                StringAssert.Contains(nfo, "<uniqueid type=\"imdb\">tt1234567</uniqueid>");
                StringAssert.Contains(nfo, "<id IMDb=\"tt1234567\">tt1234567</id>");
                Assert.IsFalse(nfo.Contains("<tmdbid>", StringComparison.OrdinalIgnoreCase));
                Assert.IsFalse(nfo.Contains("type=\"tmdb\"", StringComparison.OrdinalIgnoreCase));
                Assert.IsFalse(nfo.Contains("TMDB=", StringComparison.OrdinalIgnoreCase));
                Assert.IsFalse(nfo.Contains("themoviedb.org", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }

        [TestMethod]
        public void TryGetTmdbId_WhenPrivateAndOfficialConflict_ShouldPreferPrivateId()
        {
            var series = CreateSeries();
            series.SetProviderId(MetadataProvider.Tmdb, "123456");
            series.SetProviderId(BaseProvider.MetaSharkTmdbProviderId, "654321");

            Assert.IsTrue(series.TryGetTmdbId(out var tmdbId));
            Assert.AreEqual("654321", tmdbId);

            var providerIds = new Dictionary<string, string>
            {
                [MetadataProvider.Tmdb.ToString()] = "123456",
                [BaseProvider.MetaSharkTmdbProviderId] = "654321",
            };

            Assert.IsTrue(providerIds.TryGetTmdbId(out tmdbId));
            Assert.AreEqual("654321", tmdbId);
        }

        [TestMethod]
        public void TryGetTmdbId_WhenPrivateIdIsNonNumeric_ShouldPreservePrivatePrecedence()
        {
            var series = CreateSeries();
            series.SetProviderId(BaseProvider.MetaSharkTmdbProviderId, "abc");
            series.SetProviderId(MetadataProvider.Tmdb, "123456");

            Assert.IsTrue(series.TryGetTmdbId(out var tmdbId));
            Assert.AreEqual("abc", tmdbId);
        }

        [TestMethod]
        public void TryGetTmdbId_WhenMetaSharkProviderIdHasTmdbPrefix_ShouldReadPrefixedId()
        {
            var series = CreateSeries();
            series.SetProviderId(MetaSharkPlugin.ProviderId, "Tmdb_123456");

            Assert.IsTrue(series.TryGetTmdbId(out var tmdbId));
            Assert.AreEqual("123456", tmdbId);
        }

        [TestMethod]
        public async Task MigrateItemAsync_NonSeries_ShouldSkipWithoutPersisting()
        {
            var service = CreateService();
            var movie = new Movie
            {
                Id = Guid.NewGuid(),
                Name = "Movie",
            };
            movie.SetProviderId(MetadataProvider.Tmdb, "123456");

            var migrated = await service.MigrateItemAsync(movie, "Test", CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(migrated);
            Assert.AreEqual("123456", movie.GetProviderId(MetadataProvider.Tmdb));
        }

        [TestMethod]
        public async Task MigrateItemAsync_NfoSymlink_ShouldSkipCleanup()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"metashark-series-tmdb-nfo-link-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(tempRoot);
                var targetPath = Path.Combine(tempRoot, "target.nfo");
                var nfoPath = Path.Combine(tempRoot, "tvshow.nfo");
                File.WriteAllText(targetPath, "<tvshow>\n  <tmdbid>123456</tmdbid>\n</tvshow>\n");
                try
                {
                    File.CreateSymbolicLink(nfoPath, targetPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    Assert.Inconclusive($"当前环境不支持创建符号链接: {ex.GetType().Name}");
                    return;
                }

                var service = CreateService();
                var series = CreateSeries();
                series.Path = tempRoot;

                var migrated = await service.MigrateItemAsync(series, "Test", CancellationToken.None).ConfigureAwait(false);

                Assert.IsFalse(migrated);
                Assert.AreEqual("<tvshow>\n  <tmdbid>123456</tmdbid>\n</tvshow>\n", File.ReadAllText(targetPath));
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }

        [TestMethod]
        public async Task MigrateItemAsync_LargeNfo_ShouldSkipCleanup()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"metashark-series-tmdb-nfo-large-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(tempRoot);
                var nfoPath = Path.Combine(tempRoot, "tvshow.nfo");
                File.WriteAllText(nfoPath, "<tvshow>\n  <tmdbid>123456</tmdbid>\n" + new string('x', 5 * 1024 * 1024) + "\n</tvshow>\n");

                var service = CreateService();
                var series = CreateSeries();
                series.Path = tempRoot;

                var migrated = await service.MigrateItemAsync(series, "Test", CancellationToken.None).ConfigureAwait(false);

                Assert.IsFalse(migrated);
                StringAssert.Contains(File.ReadAllText(nfoPath), "<tmdbid>123456</tmdbid>");
            }
            finally
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
        }

        [TestMethod]
        public async Task MigrateFullLibraryAsync_ShouldQuerySeriesAndMigrateOnlyOfficialTmdbResidue()
        {
            InternalItemsQuery? capturedQuery = null;
            var seriesWithOfficialTmdb = CreateSeries();
            seriesWithOfficialTmdb.SetProviderId(MetadataProvider.Tmdb, "123456");

            var seriesWithoutOfficialTmdb = CreateSeries();
            seriesWithoutOfficialTmdb.SetProviderId(BaseProvider.MetaSharkTmdbProviderId, "654321");

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Callback<InternalItemsQuery>(query => capturedQuery = query)
                .Returns(new List<BaseItem> { seriesWithOfficialTmdb, seriesWithoutOfficialTmdb });

            var service = CreateService(libraryManagerStub.Object);

            var migratedCount = await service.MigrateFullLibraryAsync("StartupScan", CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, migratedCount);
            Assert.IsNotNull(capturedQuery);
            CollectionAssert.AreEquivalent(new[] { BaseItemKind.Series }, capturedQuery!.IncludeItemTypes!.ToArray());
            Assert.IsFalse(capturedQuery.IsVirtualItem ?? true);
            Assert.IsFalse(capturedQuery.IsMissing ?? true);
            Assert.IsTrue(capturedQuery.Recursive);
            Assert.IsNull(seriesWithOfficialTmdb.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("123456", seriesWithOfficialTmdb.GetProviderId(BaseProvider.MetaSharkTmdbProviderId));
            Assert.AreEqual(1, seriesWithOfficialTmdb.UpdateToRepositoryCallCount);
            Assert.AreEqual(0, seriesWithoutOfficialTmdb.UpdateToRepositoryCallCount);
        }

        private static SeriesTmdbProviderIdMigrationService CreateService(ILibraryManager? libraryManager = null)
        {
            return new SeriesTmdbProviderIdMigrationService(
                libraryManager ?? Mock.Of<ILibraryManager>(),
                Mock.Of<ILogger<SeriesTmdbProviderIdMigrationService>>());
        }

        private static TrackingSeries CreateSeries()
        {
            return new TrackingSeries
            {
                Id = Guid.NewGuid(),
                Name = "Series",
                Path = "/library/tv/series",
            };
        }

        private sealed class TrackingSeries : Series
        {
            public int MetadataChangedCallCount { get; private set; }

            public int UpdateToRepositoryCallCount { get; private set; }

            public ItemUpdateType? LastUpdateReason { get; private set; }

            public override ItemUpdateType OnMetadataChanged()
            {
                this.MetadataChangedCallCount++;
                return ItemUpdateType.MetadataEdit;
            }

            public override Task UpdateToRepositoryAsync(ItemUpdateType updateReason, CancellationToken cancellationToken)
            {
                this.UpdateToRepositoryCallCount++;
                this.LastUpdateReason = updateReason;
                return Task.CompletedTask;
            }
        }
    }
}
