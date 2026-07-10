using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Moq;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using TMDbLib.Objects.General;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [DoNotParallelize]
    public class TmdbApiAlternativeTitlesTest
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "metashark-tmdb-alternative-title-tests");
        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder => { });

        [TestInitialize]
        public void Initialize()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(new PluginConfiguration());
        }

        [TestCleanup]
        public void Cleanup()
        {
            ReplacePluginConfiguration(new PluginConfiguration());
        }

        [TestMethod]
        public async Task MovieRegionalAlternativeTitle_SelectsExactRequestedRegion()
        {
            using var server = new StaticJsonTcpServer("""
                {"id":42,"titles":[{"iso_3166_1":"CN","title":"简体片名"},{"iso_3166_1":"TW","title":"繁體片名"}]}
                """);
            using var api = this.CreateApi(server.BaseUrl);

            var first = await api.GetMovieRegionalAlternativeTitleAsync(42, "TW", CancellationToken.None).ConfigureAwait(false);
            var second = await api.GetMovieRegionalAlternativeTitleAsync(42, "TW", CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(server.RequestTarget);
            StringAssert.StartsWith(server.RequestTarget!, "/3/movie/42/alternative_titles");
            StringAssert.Contains(server.RequestTarget!, "country=TW");
            Assert.AreEqual("繁體片名", first);
            Assert.AreEqual(first, second);
        }

        [TestMethod]
        public async Task SeriesRegionalAlternativeTitle_DoesNotCrossRegions()
        {
            using var server = new StaticJsonTcpServer("""
                {"id":114410,"results":[{"iso_3166_1":"CN","title":"电锯人"},{"iso_3166_1":"HK","title":"鏈鋸人"}]}
                """);
            using var api = this.CreateApi(server.BaseUrl);

            var title = await api.GetSeriesRegionalAlternativeTitleAsync(114410, "TW", CancellationToken.None).ConfigureAwait(false);

            Assert.IsNull(title);
        }

        [DataTestMethod]
        [DataRow("US")]
        [DataRow("")]
        public async Task RegionalAlternativeTitle_RejectsUnsupportedRegion(string region)
        {
            using var api = new TmdbApi(this.loggerFactory);

            Assert.IsNull(await api.GetMovieRegionalAlternativeTitleAsync(42, region, CancellationToken.None).ConfigureAwait(false));
            Assert.IsNull(await api.GetSeriesRegionalAlternativeTitleAsync(42, region, CancellationToken.None).ConfigureAwait(false));
        }

        private TmdbApi CreateApi(string baseUrl)
        {
            ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableTmdb = true,
                TmdbApiKey = "test-key",
                TmdbHost = baseUrl,
            });
            var api = new TmdbApi(this.loggerFactory);
            var clientField = typeof(TmdbApi).GetField("tmDbClient", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(clientField);
            var client = clientField!.GetValue(api);
            Assert.IsNotNull(client);
            var setConfig = client!.GetType().GetMethod("SetConfig", new[] { typeof(TMDbConfig) });
            Assert.IsNotNull(setConfig);
            setConfig!.Invoke(client, new object[] { new TMDbConfig() });
            return api;
        }

        private static void EnsurePluginInstance()
        {
            if (MetaSharkPlugin.Instance != null)
            {
                return;
            }

            var pluginsPath = Path.Combine(Root, "plugins");
            var configurationsPath = Path.Combine(Root, "configurations");
            Directory.CreateDirectory(pluginsPath);
            Directory.CreateDirectory(configurationsPath);
            var appHost = new Mock<IServerApplicationHost>();
            appHost.Setup(x => x.GetLocalApiUrl(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()))
                .Returns("http://127.0.0.1:8096");
            var paths = new Mock<IApplicationPaths>();
            paths.SetupGet(x => x.PluginsPath).Returns(pluginsPath);
            paths.SetupGet(x => x.PluginConfigurationsPath).Returns(configurationsPath);
            _ = new MetaSharkPlugin(appHost.Object, paths.Object, new Mock<IXmlSerializer>().Object);
        }

        private static void ReplacePluginConfiguration(PluginConfiguration configuration)
        {
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);
            var type = plugin!.GetType();
            while (type != null)
            {
                var property = type.GetProperty("Configuration", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property?.SetMethod != null && property.PropertyType.IsAssignableFrom(typeof(PluginConfiguration)))
                {
                    property.SetValue(plugin, configuration);
                    return;
                }

                var field = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(candidate => candidate.FieldType.IsAssignableFrom(typeof(PluginConfiguration)));
                if (field != null)
                {
                    field.SetValue(plugin, configuration);
                    return;
                }

                type = type.BaseType;
            }

            Assert.Fail("Could not replace plugin configuration.");
        }

        private sealed class StaticJsonTcpServer : IDisposable
        {
            private readonly string body;
            private readonly TcpListener listener;
            private readonly Task serveTask;

            public StaticJsonTcpServer(string body)
            {
                this.body = body;
                this.listener = new TcpListener(IPAddress.Loopback, 0);
                this.listener.Start();
                this.BaseUrl = $"http://127.0.0.1:{((IPEndPoint)this.listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture)}";
                this.serveTask = Task.Run(this.ServeAsync);
            }

            public string BaseUrl { get; }

            public string? RequestTarget { get; private set; }

            public void Dispose()
            {
                this.listener.Stop();
                try
                {
                    this.serveTask.GetAwaiter().GetResult();
                }
                catch (SocketException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                catch (IOException)
                {
                }
            }

            private async Task ServeAsync()
            {
                using var client = await this.listener.AcceptTcpClientAsync().ConfigureAwait(false);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                var requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync().ConfigureAwait(false)))
                {
                }

                this.RequestTarget = requestLine?.Split(' ').ElementAtOrDefault(1);
                var bodyBytes = Encoding.UTF8.GetBytes(this.body);
                var headerBytes = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "
                    + bodyBytes.Length.ToString(CultureInfo.InvariantCulture)
                    + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headerBytes).ConfigureAwait(false);
                await stream.WriteAsync(bodyBytes).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
        }
    }
}
