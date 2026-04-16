using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Core;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TMDbLib.Objects.General;
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class EpisodeImageProviderTest
    {
        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
            builder.AddSimpleConsole(options =>
            {
                options.IncludeScopes = true;
                options.SingleLine = true;
                options.TimestampFormat = "hh:mm:ss ";
            }));

        [TestMethod]
        public void GetImages_ReportsStructuralHardMissForInvalidEpisodeNumber()
        {
            RequireOutcomeReporterContract();

            var libraryManagerStub = new Mock<ILibraryManager>();
            var outcomeReporterStub = new Mock<ITvImageRefillOutcomeReporter>();

            WithLibraryManager(libraryManagerStub.Object, () =>
            {
                var provider = CreateProvider(libraryManagerStub.Object, outcomeReporterStub.Object);
                var info = new MediaBrowser.Controller.Entities.TV.Episode
                {
                    Name = "Spice and Wolf",
                    PreferredMetadataLanguage = "zh",
                    ParentIndexNumber = 1,
                    IndexNumber = 0,
                };
                SetSeries(
                    info,
                    libraryManagerStub,
                    new MediaBrowser.Controller.Entities.TV.Series
                    {
                        Id = Guid.NewGuid(),
                        Name = "Spice and Wolf",
                        PreferredMetadataLanguage = "zh",
                        ProviderIds = new Dictionary<string, string>
                        {
                            { MetadataProvider.Tmdb.ToString(), "26707" },
                        },
                    });

                Task.Run(async () =>
                {
                    var result = await provider.GetImages(info, CancellationToken.None);
                    Assert.IsNotNull(result);
                    Assert.AreEqual(0, result.Count());
                }).GetAwaiter().GetResult();

                outcomeReporterStub.Verify(x => x.ReportHardMiss(info, "InvalidEpisodeNumber"), Times.Once);
                outcomeReporterStub.Verify(x => x.ReportSuccess(It.IsAny<BaseItem>()), Times.Never);
            });
        }

        [TestMethod]
        public void GetImages_ReportsStructuralHardMissForMissingTmdbId()
        {
            RequireOutcomeReporterContract();

            var libraryManagerStub = new Mock<ILibraryManager>();
            var outcomeReporterStub = new Mock<ITvImageRefillOutcomeReporter>();

            WithLibraryManager(libraryManagerStub.Object, () =>
            {
                var provider = CreateProvider(libraryManagerStub.Object, outcomeReporterStub.Object);
                var info = new MediaBrowser.Controller.Entities.TV.Episode
                {
                    Name = "Spice and Wolf",
                    PreferredMetadataLanguage = "zh",
                    ParentIndexNumber = 1,
                    IndexNumber = 1,
                };
                SetSeries(
                    info,
                    libraryManagerStub,
                    new MediaBrowser.Controller.Entities.TV.Series
                    {
                        Id = Guid.NewGuid(),
                        Name = "Spice and Wolf",
                        PreferredMetadataLanguage = "zh",
                        ProviderIds = new Dictionary<string, string>(),
                    });

                Task.Run(async () =>
                {
                    var result = await provider.GetImages(info, CancellationToken.None);
                    Assert.IsNotNull(result);
                    Assert.AreEqual(0, result.Count());
                }).GetAwaiter().GetResult();

                outcomeReporterStub.Verify(x => x.ReportHardMiss(info, "MissingSeriesTmdbId"), Times.Once);
                outcomeReporterStub.Verify(x => x.ReportSuccess(It.IsAny<BaseItem>()), Times.Never);
            });
        }

        [TestMethod]
        public void GetImages_ReportsSuccessForValidEpisode()
        {
            RequireOutcomeReporterContract();

            var libraryManagerStub = new Mock<ILibraryManager>();
            var outcomeReporterStub = new Mock<ITvImageRefillOutcomeReporter>();

            WithLibraryManager(libraryManagerStub.Object, () =>
            {
                var provider = CreateProvider(libraryManagerStub.Object, outcomeReporterStub.Object);
                var info = new MediaBrowser.Controller.Entities.TV.Episode
                {
                    Name = "Spice and Wolf",
                    PreferredMetadataLanguage = "zh",
                    ParentIndexNumber = 1,
                    IndexNumber = 1,
                };
                SetSeries(
                    info,
                    libraryManagerStub,
                    new MediaBrowser.Controller.Entities.TV.Series
                    {
                        Id = Guid.NewGuid(),
                        Name = "Spice and Wolf",
                        PreferredMetadataLanguage = "zh",
                        ProviderIds = new Dictionary<string, string>
                        {
                            { MetadataProvider.Tmdb.ToString(), "26707" },
                        },
                    });

                Task.Run(async () =>
                {
                    try
                    {
                        var result = await provider.GetImages(info, CancellationToken.None);
                        Assert.IsNotNull(result);
                        Assert.IsTrue(result.Any());
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests
                        || ex.Message.Contains("429", StringComparison.Ordinal))
                    {
                        Assert.Inconclusive("TMDb rate limited (429)." + ex.Message);
                    }
                }).GetAwaiter().GetResult();

                outcomeReporterStub.Verify(x => x.ReportSuccess(info), Times.Once);
                outcomeReporterStub.Verify(x => x.ReportHardMiss(It.IsAny<BaseItem>(), It.IsAny<string>()), Times.Never);
            });
        }

        [TestMethod]
        public void GetImages_ReportsSuccessForSeededControlEpisodeWithStillPath()
        {
            RequireOutcomeReporterContract();

            var libraryManagerStub = new Mock<ILibraryManager>();
            var outcomeReporterStub = new Mock<ITvImageRefillOutcomeReporter>();

            WithLibraryManager(libraryManagerStub.Object, () =>
            {
                var tmdbApi = CreateConfiguredTmdbApi();
                SeedTmdbEpisode(tmdbApi, 273467, 1, 1, "zh", "/cKzunf5OUndOIwEjcfMtsZsBO3o.jpg", 6.0d, 2);
                var provider = CreateProvider(libraryManagerStub.Object, outcomeReporterStub.Object, tmdbApi);
                var info = new MediaBrowser.Controller.Entities.TV.Episode
                {
                    Name = "女骑士成了败北俘虏",
                    PreferredMetadataLanguage = "zh",
                    ParentIndexNumber = 1,
                    IndexNumber = 1,
                };
                SetSeries(
                    info,
                    libraryManagerStub,
                    new MediaBrowser.Controller.Entities.TV.Series
                    {
                        Id = Guid.NewGuid(),
                        Name = "女骑士成为蛮族新娘",
                        PreferredMetadataLanguage = "zh",
                        ProviderIds = new Dictionary<string, string>
                        {
                            { MetadataProvider.Tmdb.ToString(), "273467" },
                        },
                    });

                Task.Run(async () =>
                {
                    var result = (await provider.GetImages(info, CancellationToken.None)).ToList();
                    Assert.AreEqual(1, result.Count);
                    Assert.AreEqual(ImageType.Primary, result[0].Type);
                    Assert.AreEqual("https://image.tmdb.org/t/p/w300/cKzunf5OUndOIwEjcfMtsZsBO3o.jpg", result[0].Url);
                }).GetAwaiter().GetResult();

                outcomeReporterStub.Verify(x => x.ReportSuccess(info), Times.Once);
                outcomeReporterStub.Verify(x => x.ReportHardMiss(It.IsAny<BaseItem>(), It.IsAny<string>()), Times.Never);
            });
        }

        [TestMethod]
        public void GetImages_ReportsHardMissForSeededFailedEpisodeWithoutStillPath()
        {
            RequireOutcomeReporterContract();

            var libraryManagerStub = new Mock<ILibraryManager>();
            var outcomeReporterStub = new Mock<ITvImageRefillOutcomeReporter>();

            WithLibraryManager(libraryManagerStub.Object, () =>
            {
                var tmdbApi = CreateConfiguredTmdbApi();
                SeedTmdbEpisode(tmdbApi, 273467, 1, 2, "zh", null, 0d, 0);
                SeedTmdbEpisodeImages(tmdbApi, 273467, 1, 2, "zh", Array.Empty<ImageData>());
                var provider = CreateProvider(libraryManagerStub.Object, outcomeReporterStub.Object, tmdbApi);
                var info = new MediaBrowser.Controller.Entities.TV.Episode
                {
                    Name = "无知是万恶之源",
                    PreferredMetadataLanguage = "zh",
                    ParentIndexNumber = 1,
                    IndexNumber = 2,
                };
                SetSeries(
                    info,
                    libraryManagerStub,
                    new MediaBrowser.Controller.Entities.TV.Series
                    {
                        Id = Guid.NewGuid(),
                        Name = "女骑士成为蛮族新娘",
                        PreferredMetadataLanguage = "zh",
                        ProviderIds = new Dictionary<string, string>
                        {
                            { MetadataProvider.Tmdb.ToString(), "273467" },
                        },
                    });

                Task.Run(async () =>
                {
                    var result = await provider.GetImages(info, CancellationToken.None);
                    Assert.IsNotNull(result);
                    Assert.AreEqual(0, result.Count());
                }).GetAwaiter().GetResult();

                outcomeReporterStub.Verify(x => x.ReportHardMiss(info, "NoStillPath"), Times.Once);
                outcomeReporterStub.Verify(x => x.ReportSuccess(It.IsAny<BaseItem>()), Times.Never);
            });
        }

        [TestMethod]
        public void GetImages_ReportsSuccessWhenStillPathMissingButEpisodeStillImagesExist()
        {
            RequireOutcomeReporterContract();

            var libraryManagerStub = new Mock<ILibraryManager>();
            var outcomeReporterStub = new Mock<ITvImageRefillOutcomeReporter>();

            WithLibraryManager(libraryManagerStub.Object, () =>
            {
                var tmdbApi = CreateConfiguredTmdbApi();
                SeedTmdbEpisode(
                    tmdbApi,
                    273467,
                    1,
                    2,
                    "zh",
                    null,
                    0d,
                    0,
                    new List<ImageData>
                    {
                        CreateImageData("/AgVyylbwMmSUW9Fp3wWKEPGT5vH.jpg", "zh", 1920, 1080),
                    });
                var provider = CreateProvider(libraryManagerStub.Object, outcomeReporterStub.Object, tmdbApi);
                var info = new MediaBrowser.Controller.Entities.TV.Episode
                {
                    Name = "无知是万恶之源",
                    PreferredMetadataLanguage = "zh",
                    ParentIndexNumber = 1,
                    IndexNumber = 2,
                };
                SetSeries(
                    info,
                    libraryManagerStub,
                    new MediaBrowser.Controller.Entities.TV.Series
                    {
                        Id = Guid.NewGuid(),
                        Name = "女骑士成为蛮族新娘",
                        PreferredMetadataLanguage = "zh",
                        ProviderIds = new Dictionary<string, string>
                        {
                            { MetadataProvider.Tmdb.ToString(), "273467" },
                        },
                    });

                List<RemoteImageInfo>? result = null;
                Task.Run(async () =>
                {
                    result = (await provider.GetImages(info, CancellationToken.None)).ToList();
                }).GetAwaiter().GetResult();

                outcomeReporterStub.Verify(x => x.ReportHardMiss(info, "NoStillPath"), Times.Never);
                outcomeReporterStub.Verify(x => x.ReportSuccess(info), Times.Once);
                Assert.IsNotNull(result);
                Assert.AreEqual(1, result!.Count);
                Assert.AreEqual(ImageType.Primary, result[0].Type);
                Assert.AreEqual("https://image.tmdb.org/t/p/w300/AgVyylbwMmSUW9Fp3wWKEPGT5vH.jpg", result[0].Url);
            });
        }

        [TestMethod]
        public void GetImages_ReportsSuccessWhenEpisodeDetailsStillSourcesAreEmptyButDedicatedEpisodeImagesExist()
        {
            RequireOutcomeReporterContract();

            var libraryManagerStub = new Mock<ILibraryManager>();
            var outcomeReporterStub = new Mock<ITvImageRefillOutcomeReporter>();

            WithLibraryManager(libraryManagerStub.Object, () =>
            {
                var tmdbApi = CreateConfiguredTmdbApi();
                SeedTmdbEpisode(
                    tmdbApi,
                    273467,
                    1,
                    2,
                    "zh",
                    null,
                    0d,
                    0,
                    Array.Empty<ImageData>());
                SeedTmdbEpisodeImages(
                    tmdbApi,
                    273467,
                    1,
                    2,
                    "zh",
                    new List<ImageData>
                    {
                        CreateImageData("/AgVyylbwMmSUW9Fp3wWKEPGT5vH.jpg", "zh", 1920, 1080),
                    });
                var provider = CreateProvider(libraryManagerStub.Object, outcomeReporterStub.Object, tmdbApi);
                var info = new MediaBrowser.Controller.Entities.TV.Episode
                {
                    Name = "无知是万恶之源",
                    PreferredMetadataLanguage = "zh",
                    ParentIndexNumber = 1,
                    IndexNumber = 2,
                };
                SetSeries(
                    info,
                    libraryManagerStub,
                    new MediaBrowser.Controller.Entities.TV.Series
                    {
                        Id = Guid.NewGuid(),
                        Name = "女骑士成为蛮族新娘",
                        PreferredMetadataLanguage = "zh",
                        ProviderIds = new Dictionary<string, string>
                        {
                            { MetadataProvider.Tmdb.ToString(), "273467" },
                        },
                    });

                List<RemoteImageInfo>? result = null;
                Task.Run(async () =>
                {
                    result = (await provider.GetImages(info, CancellationToken.None)).ToList();
                }).GetAwaiter().GetResult();

                outcomeReporterStub.Verify(x => x.ReportHardMiss(info, "NoStillPath"), Times.Never);
                outcomeReporterStub.Verify(x => x.ReportSuccess(info), Times.Once);
                Assert.IsNotNull(result);
                Assert.AreEqual(1, result!.Count);
                Assert.AreEqual(ImageType.Primary, result[0].Type);
                Assert.AreEqual("https://image.tmdb.org/t/p/w300/AgVyylbwMmSUW9Fp3wWKEPGT5vH.jpg", result[0].Url);
            });
        }

        private EpisodeImageProvider CreateProvider(ILibraryManager libraryManager, ITvImageRefillOutcomeReporter? outcomeReporter = null, TmdbApi? tmdbApi = null)
        {
            var httpClientFactory = new DefaultHttpClientFactory();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(this.loggerFactory);
            tmdbApi ??= new TmdbApi(this.loggerFactory);
            var omdbApi = new OmdbApi(this.loggerFactory);
            var imdbApi = new ImdbApi(this.loggerFactory);

            return new EpisodeImageProvider(
                httpClientFactory,
                this.loggerFactory,
                libraryManager,
                httpContextAccessorStub.Object,
                doubanApi,
                tmdbApi,
                omdbApi,
                imdbApi,
                outcomeReporter);
        }

        private TmdbApi CreateConfiguredTmdbApi()
        {
            var tmdbApi = new TmdbApi(this.loggerFactory);
            ConfigureTmdbImageConfig(tmdbApi);
            return tmdbApi;
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

        private static void RequireOutcomeReporterContract()
        {
            Assert.IsTrue(
                HasConstructorParameterFragment(typeof(EpisodeImageProvider), "Reporter"),
                "EpisodeImageProvider needs an outcome reporter abstraction before structural hard-miss and success classification can be verified.");
        }

        private static void ConfigureTmdbImageConfig(TmdbApi tmdbApi)
        {
            var tmdbClientField = typeof(TmdbApi).GetField("tmDbClient", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(tmdbClientField);

            var tmdbClient = tmdbClientField!.GetValue(tmdbApi);
            Assert.IsNotNull(tmdbClient);

            var setConfigMethod = tmdbClient!.GetType().GetMethod("SetConfig", new[] { typeof(TMDbConfig) });
            Assert.IsNotNull(setConfigMethod);

            setConfigMethod!.Invoke(tmdbClient, new object[]
            {
                new TMDbConfig
                {
                    Images = new ConfigImageTypes
                    {
                        BaseUrl = "http://image.tmdb.org/t/p/",
                        SecureBaseUrl = "https://image.tmdb.org/t/p/",
                        PosterSizes = new List<string> { "w500" },
                        BackdropSizes = new List<string> { "w780" },
                        LogoSizes = new List<string> { "w500" },
                        ProfileSizes = new List<string> { "w500" },
                        StillSizes = new List<string> { "w300" },
                    },
                },
            });
        }

        private static IMemoryCache GetTmdbMemoryCache(TmdbApi tmdbApi)
        {
            var memoryCacheField = typeof(TmdbApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(memoryCacheField, "TmdbApi.memoryCache 未定义");

            var memoryCache = memoryCacheField!.GetValue(tmdbApi) as IMemoryCache;
            Assert.IsNotNull(memoryCache, "TmdbApi.memoryCache 不是有效的 IMemoryCache");
            return memoryCache!;
        }

        private static void SeedTmdbEpisode(TmdbApi tmdbApi, int seriesTmdbId, int seasonNumber, int episodeNumber, string language, string? stillPath, double voteAverage, int voteCount, IReadOnlyCollection<ImageData>? stillImages = null)
        {
            var episode = new TvEpisode
            {
                Name = $"Seeded Episode {episodeNumber}",
                StillPath = stillPath,
                VoteAverage = voteAverage,
                VoteCount = voteCount,
                Images = stillImages == null
                    ? null
                    : new StillImages
                    {
                        Stills = stillImages.ToList(),
                    },
            };

            GetTmdbMemoryCache(tmdbApi).Set(
                $"episode-{seriesTmdbId}-s{seasonNumber}e{episodeNumber}-{language}-{language}",
                episode,
                TimeSpan.FromMinutes(5));
        }

        private static void SeedTmdbEpisodeImages(TmdbApi tmdbApi, int seriesTmdbId, int seasonNumber, int episodeNumber, string language, IReadOnlyCollection<ImageData> stillImages)
        {
            var images = new StillImages
            {
                Id = episodeNumber,
                Stills = stillImages.ToList(),
            };

            GetTmdbMemoryCache(tmdbApi).Set(
                $"episode-images-{seriesTmdbId}-s{seasonNumber}e{episodeNumber}-{language}-{language}",
                images,
                TimeSpan.FromMinutes(5));
        }

        private static ImageData CreateImageData(string filePath, string? language, int width, int height)
        {
            return new ImageData
            {
                FilePath = filePath,
                Iso_639_1 = language,
                Width = width,
                Height = height,
                VoteAverage = 8.5,
                VoteCount = 10,
            };
        }

        private static void SetSeries(MediaBrowser.Controller.Entities.TV.Episode episode, Mock<ILibraryManager> libraryManagerStub, MediaBrowser.Controller.Entities.TV.Series series)
        {
            libraryManagerStub.Setup(x => x.GetItemById(series.Id)).Returns((BaseItem)series);
            episode.SeriesId = series.Id;
            episode.SeriesName = series.Name ?? string.Empty;
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
