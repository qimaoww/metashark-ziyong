using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Reflection;
using Jellyfin.Plugin.MetaShark;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Api.Http;
using Jellyfin.Plugin.MetaShark.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using TMDbLib.Objects.General;
using TMDbLib.Objects.Search;

namespace Jellyfin.Plugin.MetaShark.Test;

[TestClass]
[DoNotParallelize]
public class ApiConfigurationContractTest
{
    private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-api-contract-tests");
    private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
    private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");
    private static readonly ILoggerFactory LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });

    [TestInitialize]
    public void SetUp()
    {
        EnsurePluginInstance();
        ReplacePluginConfiguration(new PluginConfiguration());
    }

    [TestMethod]
    [TestCategory("Stable")]
    public void TmdbApi_CapturesApiKeyAndHostAtConstruction()
    {
        ReplacePluginConfiguration(new PluginConfiguration
        {
            TmdbApiKey = "first-key",
            TmdbHost = "first.tmdb.local",
        });

        using var api = new TmdbApi(LoggerFactory);

        ReplacePluginConfiguration(new PluginConfiguration
        {
            TmdbApiKey = "second-key",
            TmdbHost = "second.tmdb.local",
        });

        Assert.AreEqual("first-key", GetPrivateField<string>(api, "apiKey"));
        Assert.AreEqual("first.tmdb.local", GetPrivateField<string>(api, "apiHost"));
    }

    [TestMethod]
    [TestCategory("Stable")]
    public async Task TmdbApi_SearchMovieReadsEnableAdultFromLiveConfiguration()
    {
        ReplacePluginConfiguration(new PluginConfiguration
        {
            EnableTmdbAdult = false,
        });
        using var api = new TmdbApi(LoggerFactory);
        var handler = new CapturingTmdbHandler();
        ConfigureTmdbClient(api, new HttpClient(handler));

        MetaSharkPlugin.Instance!.Configuration.EnableTmdbAdult = true;

        _ = await api.SearchMovieAsync("adult-live-contract", 2024, "zh-CN", CancellationToken.None)
            .ConfigureAwait(false);

        Assert.IsNotNull(handler.LastRequestUri, "TMDb search did not issue a request through the injected handler.");
        StringAssert.Contains(handler.LastRequestUri!.Query, "include_adult=true");
    }

    [TestMethod]
    [TestCategory("Stable")]
    public void TmdbApi_CurrentCacheKeysRemainObservableStrings()
    {
        using var api = new TmdbApi(LoggerFactory);
        var memoryCache = GetPrivateField<MemoryCache>(api, "memoryCache");

        var movieResults = new SearchContainer<SearchMovie>
        {
            Results = new List<SearchMovie>
            {
                new SearchMovie { Id = 123, Title = "cached movie" },
            },
        };
        var seriesResults = new SearchContainer<SearchTv>
        {
            Results = new List<SearchTv>
            {
                new SearchTv { Id = 456, Name = "cached series" },
            },
        };

        memoryCache.Set("moviesearch-Cache Name-1999-zh-CN", movieResults, TimeSpan.FromMinutes(5));
        memoryCache.Set("searchseries-Cache Name-zh-CN", seriesResults, TimeSpan.FromMinutes(5));

        var movies = api.SearchMovieAsync("Cache Name", 1999, "zh-CN", CancellationToken.None).GetAwaiter().GetResult();
        var series = api.SearchSeriesAsync("Cache Name", "zh-CN", CancellationToken.None).GetAwaiter().GetResult();

        Assert.AreSame(movieResults.Results, movies);
        Assert.AreSame(seriesResults.Results, series);
    }

    [TestMethod]
    [TestCategory("Stable")]
    public void HttpClientHandlerExtended_CurrentTlsValidationIsPermissive()
    {
        using var handler = new HttpClientHandlerExtended();

        Assert.IsNotNull(handler.ServerCertificateCustomValidationCallback);
        Assert.IsTrue(handler.ServerCertificateCustomValidationCallback!(
            new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/"),
            null,
            null,
            SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [TestMethod]
    [TestCategory("Stable")]
    public void DoubanApi_ReloadAddsConfiguredCookiesWithoutClearingStaleCookies()
    {
        ReplacePluginConfiguration(new PluginConfiguration
        {
            DoubanCookies = "ck=first; bid=initial",
        });
        using var api = new DoubanApi(LoggerFactory);
        var cookieContainer = GetPrivateField<CookieContainer>(api, "cookieContainer");

        MetaSharkPlugin.Instance!.Configuration.DoubanCookies = "ck=second; dbcl2=next";
        InvokePrivateInstanceMethod(api, "LoadLoadDoubanCookie");

        var cookies = cookieContainer.GetCookies(new Uri("https://www.douban.com/"));

        Assert.AreEqual("second", cookies["ck"]?.Value);
        Assert.AreEqual("initial", cookies["bid"]?.Value);
        Assert.AreEqual("next", cookies["dbcl2"]?.Value);
    }

    [TestMethod]
    [TestCategory("Stable")]
    public void DoubanApi_CurrentCacheKeysRemainObservableStrings()
    {
        using var api = new DoubanApi(LoggerFactory);
        var cachedSubject = new Jellyfin.Plugin.MetaShark.Model.DoubanSubject
        {
            Sid = "subject-1",
            Name = "Cached Subject",
            Category = "电影",
            Genre = "电影",
        };
        var memoryCache = GetPrivateField<MemoryCache>(api, "memoryCache");

        memoryCache.Set("search_缓存键", new List<Jellyfin.Plugin.MetaShark.Model.DoubanSubject> { cachedSubject }, TimeSpan.FromMinutes(5));

        var result = api.SearchAsync("缓存键", CancellationToken.None).GetAwaiter().GetResult();

        Assert.AreEqual(1, result.Count);
        Assert.AreSame(cachedSubject, result[0]);
    }

    [TestMethod]
    [TestCategory("Stable")]
    public void DoubanApi_DefaultHttpFactoryPreservesCurrentHandlerAndClientSettings()
    {
        using var api = new DoubanApi(LoggerFactory);

        var httpClient = GetPrivateField<HttpClient>(api, "httpClient");
        var httpClientHandler = GetPrivateField<HttpClientHandlerExtended>(api, "httpClientHandler");
        var doubanHandler = GetPrivateField<DoubanSecHandler>(api, "doubanHandler");
        var cookieContainer = GetPrivateField<CookieContainer>(api, "cookieContainer");
        var factory = GetPrivateField<IDoubanHttpClientFactory>(api, "httpClientFactory");

        Assert.IsInstanceOfType(factory, typeof(DefaultDoubanHttpClientFactory));
        Assert.AreSame(httpClientHandler, doubanHandler.InnerHandler);
        Assert.AreSame(httpClientHandler.CookieContainer, cookieContainer);
        Assert.AreEqual(TimeSpan.FromSeconds(20), httpClient.Timeout);
        Assert.IsTrue(httpClientHandler.CheckCertificateRevocationList);
        Assert.IsTrue(httpClientHandler.UseCookies);
        Assert.IsTrue(httpClientHandler.ServerCertificateCustomValidationCallback!(
            new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/"),
            null,
            null,
            SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.IsTrue(httpClient.DefaultRequestHeaders.UserAgent.ToString().Contains(DoubanApi.HTTPUSERAGENT));
        Assert.AreEqual("https://movie.douban.com", httpClient.DefaultRequestHeaders.GetValues("Origin").Single());
        Assert.AreEqual("https://movie.douban.com/", httpClient.DefaultRequestHeaders.Referrer?.ToString());
    }

    private static void ConfigureTmdbClient(TmdbApi api, HttpClient httpClient)
    {
        var tmdbClient = GetPrivateField<object>(api, "tmDbClient");
        var setConfigMethod = tmdbClient.GetType().GetMethod("SetConfig", new[] { typeof(TMDbConfig) });
        Assert.IsNotNull(setConfigMethod, "TMDbClient.SetConfig 未定义。");
        setConfigMethod!.Invoke(tmdbClient, new object[] { new TMDbConfig() });

        var restClient = GetPrivateField<object>(tmdbClient, "_client");
        var httpClientProperty = restClient.GetType().GetProperty("HttpClient", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.IsNotNull(httpClientProperty, "RestClient.HttpClient 未定义。");
        httpClientProperty!.SetValue(restClient, httpClient);
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, $"{instance.GetType().Name}.{fieldName} 未定义。");
        var value = field!.GetValue(instance);
        Assert.IsInstanceOfType(value, typeof(T), $"{instance.GetType().Name}.{fieldName} 类型不匹配。");
        return (T)value!;
    }

    private static void InvokePrivateInstanceMethod(object instance, string methodName)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, $"{instance.GetType().Name}.{methodName} 未定义。");
        method!.Invoke(instance, Array.Empty<object>());
    }

    private static void EnsurePluginInstance()
    {
        if (MetaSharkPlugin.Instance != null)
        {
            return;
        }

        Directory.CreateDirectory(PluginsPath);
        Directory.CreateDirectory(PluginConfigurationsPath);

        var appHost = new Mock<IServerApplicationHost>();
        appHost.Setup(x => x.GetLocalApiUrl(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>())).Returns("http://127.0.0.1:8096");
        var applicationPaths = new Mock<IApplicationPaths>();
        applicationPaths.SetupGet(x => x.PluginsPath).Returns(PluginsPath);
        applicationPaths.SetupGet(x => x.PluginConfigurationsPath).Returns(PluginConfigurationsPath);
        var xmlSerializer = new Mock<IXmlSerializer>();

        _ = new MetaSharkPlugin(appHost.Object, applicationPaths.Object, xmlSerializer.Object);
    }

    private static void ReplacePluginConfiguration(PluginConfiguration configuration)
    {
        var plugin = MetaSharkPlugin.Instance;
        Assert.IsNotNull(plugin);

        var currentType = plugin!.GetType();
        while (currentType != null)
        {
            var configurationProperty = currentType.GetProperty("Configuration", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (configurationProperty != null
                && configurationProperty.PropertyType.IsAssignableFrom(typeof(PluginConfiguration))
                && configurationProperty.SetMethod != null)
            {
                configurationProperty.SetValue(plugin, configuration);
                return;
            }

            var configurationField = currentType
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .FirstOrDefault(field => field.FieldType.IsAssignableFrom(typeof(PluginConfiguration)));
            if (configurationField != null)
            {
                configurationField.SetValue(plugin, configuration);
                return;
            }

            currentType = currentType.BaseType;
        }

        Assert.Fail("Could not replace MetaSharkPlugin configuration for tests.");
    }

    private sealed class CapturingTmdbHandler : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.LastRequestUri = request.RequestUri;
            var content = new StringContent("{\"page\":1,\"results\":[],\"total_pages\":0,\"total_results\":0}");
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        }
    }
}
