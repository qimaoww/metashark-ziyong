using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Core;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

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

        private EpisodeImageProvider CreateProvider(ILibraryManager libraryManager, ITvImageRefillOutcomeReporter? outcomeReporter = null)
        {
            var httpClientFactory = new DefaultHttpClientFactory();
            var httpContextAccessorStub = new Mock<IHttpContextAccessor>();
            var doubanApi = new DoubanApi(this.loggerFactory);
            var tmdbApi = new TmdbApi(this.loggerFactory);
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
