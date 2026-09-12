using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class BaseProviderMetadataLockTest
    {
        [TestMethod]
        public async Task TryPersistVerifiedTmdbCorrectionMetadataAsync_ItemLocked_DoesNotModifyLibraryItem()
        {
            using var loggerFactory = LoggerFactory.Create(_ => { });
            var path = "/mnt/media/Movies/Locked Movie/Locked Movie.mkv";
            var currentMovie = new TrackingMovie
            {
                Id = Guid.NewGuid(),
                IsLocked = true,
                Name = "锁定旧标题",
                OriginalTitle = "Locked Old Original",
                Overview = "锁定旧简介",
                ProductionYear = 2001,
                PremiereDate = new DateTime(2001, 1, 1),
                Path = path,
                ProviderIds = new Dictionary<string, string>
                {
                    [BaseProvider.DoubanProviderId] = "locked-douban",
                    [MetaSharkPlugin.ProviderId] = "Douban_locked-douban",
                    [MetadataProvider.Tmdb.ToString()] = "111",
                    [MetadataProvider.Imdb.ToString()] = "tt0100001",
                },
            };
            var authoritativeMovie = new Movie
            {
                Name = "权威新标题",
                OriginalTitle = "Authoritative Original",
                Overview = "权威新简介",
                ProductionYear = 2024,
                PremiereDate = new DateTime(2024, 2, 3),
                ProviderIds = new Dictionary<string, string>
                {
                    [MetadataProvider.Imdb.ToString()] = "tt0100002",
                    [MetadataProvider.Tvdb.ToString()] = "tvdbnew",
                },
            };
            var provider = CreateProvider(loggerFactory, path, currentMovie, recursive: false);

            await provider.PersistVerifiedTmdbCorrectionMetadataAsync(
                new MovieInfo { Path = path },
                "222",
                shouldUseTmdbMetadataAfterCorrection: true,
                authoritativeMovie,
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("锁定旧标题", currentMovie.Name);
            Assert.AreEqual("Locked Old Original", currentMovie.OriginalTitle);
            Assert.AreEqual("锁定旧简介", currentMovie.Overview);
            Assert.AreEqual(2001, currentMovie.ProductionYear);
            Assert.AreEqual(new DateTime(2001, 1, 1), currentMovie.PremiereDate);
            Assert.AreEqual("locked-douban", currentMovie.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Douban_locked-douban", currentMovie.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual("111", currentMovie.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("tt0100001", currentMovie.GetProviderId(MetadataProvider.Imdb));
            Assert.IsNull(currentMovie.GetProviderId(MetadataProvider.Tvdb));
            Assert.AreEqual(0, currentMovie.MetadataChangedCallCount);
            Assert.AreEqual(0, currentMovie.UpdateToRepositoryCallCount);
        }

        [TestMethod]
        public async Task TryPersistVerifiedTmdbCorrectionMetadataAsync_NameLocked_PreservesNameAndPersistsUnlockedFields()
        {
            using var loggerFactory = LoggerFactory.Create(_ => { });
            var path = "/mnt/media/Movies/Name Locked Movie/Name Locked Movie.mkv";
            var currentMovie = new TrackingMovie
            {
                Id = Guid.NewGuid(),
                LockedFields = new[] { MetadataField.Name },
                Name = "名称锁旧标题",
                OriginalTitle = "Name Locked Original",
                Overview = "旧简介",
                ProductionYear = 2001,
                PremiereDate = new DateTime(2001, 1, 1),
                Path = path,
                ProviderIds = new Dictionary<string, string>
                {
                    [BaseProvider.DoubanProviderId] = "name-locked-douban",
                    [MetaSharkPlugin.ProviderId] = "Douban_name-locked-douban",
                    [MetadataProvider.Tmdb.ToString()] = "111",
                },
            };
            var authoritativeMovie = new Movie
            {
                Name = "权威新标题",
                OriginalTitle = "Authoritative Original",
                Overview = "权威新简介",
                ProductionYear = 2024,
                PremiereDate = new DateTime(2024, 2, 3),
                ProviderIds = new Dictionary<string, string>
                {
                    [MetadataProvider.Imdb.ToString()] = "tt0100002",
                    [MetadataProvider.Tvdb.ToString()] = "tvdbnew",
                },
            };
            var provider = CreateProvider(loggerFactory, path, currentMovie, recursive: false);

            await provider.PersistVerifiedTmdbCorrectionMetadataAsync(
                new MovieInfo { Path = path },
                "222",
                shouldUseTmdbMetadataAfterCorrection: true,
                authoritativeMovie,
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("名称锁旧标题", currentMovie.Name);
            Assert.AreEqual("Name Locked Original", currentMovie.OriginalTitle);
            Assert.AreEqual("权威新简介", currentMovie.Overview);
            Assert.AreEqual(2024, currentMovie.ProductionYear);
            Assert.AreEqual(new DateTime(2024, 2, 3), currentMovie.PremiereDate);
            Assert.IsFalse(currentMovie.ProviderIds.ContainsKey(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Tmdb_222", currentMovie.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual("222", currentMovie.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("tt0100002", currentMovie.GetProviderId(MetadataProvider.Imdb));
            Assert.AreEqual("tvdbnew", currentMovie.GetProviderId(MetadataProvider.Tvdb));
            Assert.AreEqual(1, currentMovie.MetadataChangedCallCount);
            Assert.AreEqual(1, currentMovie.UpdateToRepositoryCallCount);
        }

        [TestMethod]
        public async Task TryPersistLlmTmdbCompletionProviderIdsAsync_ItemLocked_DoesNotModifyProviderIds()
        {
            using var loggerFactory = LoggerFactory.Create(_ => { });
            var path = "/mnt/media/TV/Locked Series";
            var currentSeries = new TrackingSeries
            {
                Id = Guid.NewGuid(),
                IsLocked = true,
                Name = "锁定剧集",
                Path = path,
                ProviderIds = new Dictionary<string, string>
                {
                    [BaseProvider.DoubanProviderId] = "locked-series-douban",
                    [MetaSharkPlugin.ProviderId] = "Douban_locked-series-douban",
                    [MetadataProvider.Tmdb.ToString()] = "111",
                    [MetadataProvider.Imdb.ToString()] = "tt0100001",
                    [MetadataProvider.Tvdb.ToString()] = "old-tvdb",
                },
            };
            var authoritativeSeries = new Series
            {
                ProviderIds = new Dictionary<string, string>
                {
                    [BaseProvider.DoubanProviderId] = "locked-series-douban",
                    [MetaSharkPlugin.ProviderId] = "Tmdb_222",
                    [MetadataProvider.Imdb.ToString()] = "tt0100002",
                    [MetadataProvider.Tvdb.ToString()] = "new-tvdb",
                },
            };
            var provider = CreateProvider(loggerFactory, path, currentSeries, recursive: true);

            await provider.PersistLlmTmdbCompletionProviderIdsAsync(
                new SeriesInfo { Path = path },
                "222",
                authoritativeSeries,
                CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("locked-series-douban", currentSeries.GetProviderId(BaseProvider.DoubanProviderId));
            Assert.AreEqual("Douban_locked-series-douban", currentSeries.GetProviderId(MetaSharkPlugin.ProviderId));
            Assert.AreEqual("111", currentSeries.GetProviderId(MetadataProvider.Tmdb));
            Assert.AreEqual("tt0100001", currentSeries.GetProviderId(MetadataProvider.Imdb));
            Assert.AreEqual("old-tvdb", currentSeries.GetProviderId(MetadataProvider.Tvdb));
            Assert.AreEqual(0, currentSeries.MetadataChangedCallCount);
            Assert.AreEqual(0, currentSeries.UpdateToRepositoryCallCount);
        }

        private static ExposedBaseProvider CreateProvider(ILoggerFactory loggerFactory, string path, BaseItem item, bool recursive)
        {
            var libraryManager = new Mock<ILibraryManager>();
            libraryManager.Setup(x => x.FindByPath(path, recursive)).Returns(item);

            return new ExposedBaseProvider(loggerFactory, libraryManager.Object);
        }

        private sealed class ExposedBaseProvider : BaseProvider
        {
            public ExposedBaseProvider(ILoggerFactory loggerFactory, ILibraryManager libraryManager)
                : base(
                    Mock.Of<IHttpClientFactory>(),
                    loggerFactory.CreateLogger<ExposedBaseProvider>(),
                    libraryManager,
                    Mock.Of<IHttpContextAccessor>(),
                    new DoubanApi(loggerFactory),
                    new TmdbApi(loggerFactory),
                    new OmdbApi(loggerFactory),
                    new ImdbApi(loggerFactory))
            {
            }

            public Task PersistVerifiedTmdbCorrectionMetadataAsync(
                ItemLookupInfo info,
                string tmdbId,
                bool shouldUseTmdbMetadataAfterCorrection,
                BaseItem? authoritativeMetadataItem,
                CancellationToken cancellationToken)
            {
                return this.TryPersistVerifiedTmdbCorrectionMetadataAsync(
                    info,
                    tmdbId,
                    shouldUseTmdbMetadataAfterCorrection,
                    authoritativeMetadataItem,
                    cancellationToken);
            }

            public Task PersistLlmTmdbCompletionProviderIdsAsync(
                ItemLookupInfo info,
                string? tmdbId,
                BaseItem? authoritativeMetadataItem,
                CancellationToken cancellationToken)
            {
                return this.TryPersistLlmTmdbCompletionProviderIdsAsync(
                    info,
                    tmdbId,
                    authoritativeMetadataItem,
                    cancellationToken);
            }
        }

        private sealed class TrackingMovie : Movie
        {
            public int MetadataChangedCallCount { get; private set; }

            public int UpdateToRepositoryCallCount { get; private set; }

            public override ItemUpdateType OnMetadataChanged()
            {
                this.MetadataChangedCallCount++;
                return ItemUpdateType.MetadataEdit;
            }

            public override Task UpdateToRepositoryAsync(ItemUpdateType updateReason, CancellationToken cancellationToken)
            {
                this.UpdateToRepositoryCallCount++;
                return Task.CompletedTask;
            }
        }

        private sealed class TrackingSeries : Series
        {
            public int MetadataChangedCallCount { get; private set; }

            public int UpdateToRepositoryCallCount { get; private set; }

            public override ItemUpdateType OnMetadataChanged()
            {
                this.MetadataChangedCallCount++;
                return ItemUpdateType.MetadataEdit;
            }

            public override Task UpdateToRepositoryAsync(ItemUpdateType updateReason, CancellationToken cancellationToken)
            {
                this.UpdateToRepositoryCallCount++;
                return Task.CompletedTask;
            }
        }
    }
}
