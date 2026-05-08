using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Core;
using Jellyfin.Plugin.MetaShark.Providers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Serialization;
using Moq;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TMDbLib.Objects.Find;
using TMDbLib.Objects.General;
using TMDbLib.Objects.Languages;
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [DoNotParallelize]
    public class TmdbApiTest
    {
        private TestContext? testContextInstance;

        /// <summary>
        /// Gets or sets the test context which provides
        /// information about and functionality for the current test run.
        /// </summary>
        public TestContext TestContext
        {
            get { return testContextInstance!; }
            set { testContextInstance = value; }
        }

        ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
                builder.AddSimpleConsole(options =>
                {
                    options.IncludeScopes = true;
                    options.SingleLine = true;
                    options.TimestampFormat = "hh:mm:ss ";
                }));



        [TestMethod]
        public async Task GetEpisodeAsync_WhenTmdbGeneralHttpException_ReturnsNull()
        {
            using var api = CreateApiWithTmdbStatus(HttpStatusCode.InternalServerError);

            var result = await api.GetEpisodeAsync(1, 2, 3, "zh-CN", "zh-CN", CancellationToken.None).ConfigureAwait(false);

            Assert.IsNull(result, "TMDb 单集详情遇到非预期 HTTP 错误时应返回 null。 ");
        }

        [TestMethod]
        public async Task GetEpisodeTranslationTitleAsync_WhenTmdbGeneralHttpException_ReturnsNull()
        {
            using var api = CreateApiWithTmdbStatus(HttpStatusCode.InternalServerError);

            var result = await api.GetEpisodeTranslationTitleAsync(1, 2, 3, "zh-CN", CancellationToken.None).ConfigureAwait(false);

            Assert.IsNull(result, "TMDb 单集翻译标题遇到非预期 HTTP 错误时应返回 null。 ");
        }

        [TestMethod]
        public async Task GetEpisodeTranslationOverviewAsync_WhenTmdbGeneralHttpException_ReturnsNull()
        {
            using var api = CreateApiWithTmdbStatus(HttpStatusCode.InternalServerError);

            var result = await api.GetEpisodeTranslationOverviewAsync(1, 2, 3, "zh-CN", CancellationToken.None).ConfigureAwait(false);

            Assert.IsNull(result, "TMDb 单集翻译简介遇到非预期 HTTP 错误时应返回 null。 ");
        }

        [TestMethod]
        public async Task GetEpisodeImagesAsync_WhenTmdbGeneralHttpException_ReturnsNull()
        {
            using var api = CreateApiWithTmdbStatus(HttpStatusCode.InternalServerError);

            var result = await api.GetEpisodeImagesAsync(1, 2, 3, "zh-CN", string.Empty, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(result == null || result.Stills.Count == 0, "TMDb 单集图片遇到非预期 HTTP 错误时应返回 null 或空图片集合。 ");
        }

        [TestMethod]
        public async Task GetEpisodeAsync_WhenCallerCancellationRequested_PropagatesCancellation()
        {
            using var api = CreateApiWithTmdbStatus(HttpStatusCode.InternalServerError);
            using var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.Cancel();

            await Assert.ThrowsExceptionAsync<TaskCanceledException>(async () =>
                await api.GetEpisodeAsync(1, 2, 3, "zh-CN", "zh-CN", cancellationTokenSource.Token).ConfigureAwait(false)).ConfigureAwait(false);
        }

        [TestMethod]
        public void TestGetMovie()
        {
            var api = new TmdbApi(loggerFactory);

            Task.Run(async () =>
            {
                try
                {
                    var result = await api.GetMovieAsync(752, "zh", "zh", CancellationToken.None)
               .ConfigureAwait(false);
                    Assert.IsNotNull(result);
                    TestContext.WriteLine(result.Images.ToJson());
                }
                catch (Exception ex)
                {
                    TestContext.WriteLine(ex.Message);
                }
            }).GetAwaiter().GetResult();
        }


        [TestMethod]
        public void TestGetSeries()
        {
            var api = new TmdbApi(loggerFactory);

            Task.Run(async () =>
            {
                try
                {
                    var result = await api.GetSeriesAsync(13372, "zh", "zh", CancellationToken.None)
               .ConfigureAwait(false);
                    Assert.IsNotNull(result);
                    TestContext.WriteLine(result.Images.ToJson());
                }
                catch (Exception ex)
                {
                    TestContext.WriteLine(ex.Message);
                }
            }).GetAwaiter().GetResult();
        }


        [TestMethod]
        public void TestGetEpisode()
        {
            var api = new TmdbApi(loggerFactory);

            Task.Run(async () =>
            {
                try
                {
                    var result = await api.GetEpisodeAsync(13372, 1, 1, "zh", "zh", CancellationToken.None)
               .ConfigureAwait(false);
                    Assert.IsNotNull(result);
                    TestContext.WriteLine(result.Images.Stills.ToJson());
                }
                catch (Exception ex)
                {
                    TestContext.WriteLine(ex.Message);
                }
            }).GetAwaiter().GetResult();
        }



        [TestMethod]
        public void TestSearch()
        {
            var keyword = "狼与香辛料";
            var api = new TmdbApi(loggerFactory);

            Task.Run(async () =>
            {
                try
                {
                    var result = await api.SearchSeriesAsync(keyword, "zh", CancellationToken.None).ConfigureAwait(false);
                    Assert.IsNotNull(result);
                    TestContext.WriteLine(result.ToJson());
                }
                catch (Exception ex)
                {
                    TestContext.WriteLine(ex.Message);
                }
            }).GetAwaiter().GetResult();
        }


        [TestMethod]
        public void TestFindByExternalId()
        {
            var api = new TmdbApi(loggerFactory);

            Task.Run(async () =>
            {
                try
                {
                    var result = await api.FindByExternalIdAsync("tt5924366", FindExternalSource.Imdb, "zh", CancellationToken.None)
               .ConfigureAwait(false);
                    Assert.IsNotNull(result);
                    TestContext.WriteLine(result.ToJson());
                }
                catch (Exception ex)
                {
                    TestContext.WriteLine(ex.Message);
                }
            }).GetAwaiter().GetResult();
        }


        private TmdbApi CreateApiWithTmdbStatus(HttpStatusCode statusCode)
        {
            var api = new TmdbApi(loggerFactory);
            ConfigureTmdbClient(api, new HttpClient(new StatusHttpMessageHandler(statusCode)));
            return api;
        }

        private static void ConfigureTmdbClient(TmdbApi api, HttpClient httpClient)
        {
            var tmdbClientField = typeof(TmdbApi).GetField("tmDbClient", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(tmdbClientField, "TmdbApi.tmDbClient 未定义。 ");
            var tmdbClient = tmdbClientField!.GetValue(api);
            Assert.IsNotNull(tmdbClient, "TmdbApi.tmDbClient 不是有效对象。 ");
            var setConfigMethod = tmdbClient!.GetType().GetMethod("SetConfig", new[] { typeof(TMDbConfig) });
            Assert.IsNotNull(setConfigMethod, "TMDbClient.SetConfig 未定义。 ");
            setConfigMethod!.Invoke(tmdbClient, new object[] { new TMDbConfig() });

            var restClientField = tmdbClient.GetType().GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(restClientField, "TMDbClient._client 未定义。 ");
            var restClient = restClientField!.GetValue(tmdbClient);
            Assert.IsNotNull(restClient, "TMDbClient._client 不是有效对象。 ");
            var httpClientProperty = restClient!.GetType().GetProperty("HttpClient", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(httpClientProperty, "RestClient.HttpClient 未定义。 ");
            httpClientProperty!.SetValue(restClient, httpClient);
        }

        private sealed class StatusHttpMessageHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode statusCode;

            public StatusHttpMessageHandler(HttpStatusCode statusCode)
            {
                this.statusCode = statusCode;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(new HttpResponseMessage(this.statusCode)
                {
                    Content = new StringContent("server error"),
                });
            }
        }

    }
}
