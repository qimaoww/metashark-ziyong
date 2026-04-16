using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [DoNotParallelize]
    public class TmdbApiEpisodeImagesTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-tmdb-api-episode-images-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");

        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
            builder.AddSimpleConsole(options =>
            {
                options.IncludeScopes = true;
                options.SingleLine = true;
                options.TimestampFormat = "hh:mm:ss ";
            }));

        [TestInitialize]
        public void ResetConfigurationBeforeTest()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(new PluginConfiguration());
        }

        [TestCleanup]
        public void ResetConfigurationAfterTest()
        {
            EnsurePluginInstance();
            ReplacePluginConfiguration(new PluginConfiguration());
        }

        [TestMethod]
        public void GetEpisodeImagesAsync_DeserializesFilePathFromDedicatedEpisodeImagesPayload()
        {
            EnsurePluginInstance();

            using var server = new StaticJsonTcpServer(EpisodeImagesJson);
            ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableTmdb = true,
                TmdbApiKey = "test-key",
                TmdbHost = server.BaseUrl,
            });

            using var api = new TmdbApi(this.loggerFactory);

            var images = Task.Run(async () =>
                await api.GetEpisodeImagesAsync(273467, 1, 2, "zh-CN", "zh-CN", CancellationToken.None).ConfigureAwait(false))
                .GetAwaiter().GetResult();

            Assert.IsNotNull(images, "episode-images 响应已返回 200 JSON，GetEpisodeImagesAsync 不应返回 null。");
            Assert.IsNotNull(images!.Stills, "episode-images 响应里的 stills 数组不应丢失。 ");
            Assert.AreEqual(1, images.Stills.Count, "测试 JSON 里只有一张 still，应被完整反序列化。 ");
            Assert.AreEqual("/AgVyylbwMmSUW9Fp3wWKEPGT5vH.jpg", images.Stills[0].FilePath, "snake_case 的 file_path 应被物化到 TMDbLib ImageData.FilePath。 ");
            Assert.IsNotNull(server.RequestTarget, "测试服务器应收到 dedicated episode-images 请求。 ");
            StringAssert.StartsWith(server.RequestTarget!, "/3/tv/273467/season/1/episode/2/images?api_key=test-key", "请求应命中 dedicated episode-images endpoint。 ");
            StringAssert.Contains(server.RequestTarget!, "include_image_language=zh-CN%2Czh%2Cnull%2Cen", "请求应保留当前 image-language 归一化行为。 ");
        }

        [TestMethod]
        public void GetEpisodeImagesAsync_ExpandsGenericZhImageLanguageToIncludeZhCn()
        {
            EnsurePluginInstance();

            using var server = new StaticJsonTcpServer(EpisodeImagesJson);
            ReplacePluginConfiguration(new PluginConfiguration
            {
                EnableTmdb = true,
                TmdbApiKey = "test-key",
                TmdbHost = server.BaseUrl,
            });

            using var api = new TmdbApi(this.loggerFactory);

            var images = Task.Run(async () =>
                await api.GetEpisodeImagesAsync(273467, 1, 2, "zh", "zh", CancellationToken.None).ConfigureAwait(false))
                .GetAwaiter().GetResult();

            Assert.IsNotNull(images, "episode-images 响应已返回 200 JSON，GetEpisodeImagesAsync 不应返回 null。 ");
            Assert.IsNotNull(server.RequestTarget, "测试服务器应收到 dedicated episode-images 请求。 ");
            StringAssert.Contains(server.RequestTarget!, "include_image_language=zh%2Czh-CN%2Cnull%2Cen", "generic zh 的 image-language 请求应显式带上 zh-CN，避免漏掉只在 zh-CN 形态下返回的 still。 ");
        }

        private static void EnsurePluginInstance()
        {
            if (MetaSharkPlugin.Instance != null)
            {
                EnsurePluginConfiguration();
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
            EnsurePluginConfiguration();
        }

        private static void EnsurePluginConfiguration()
        {
            var plugin = MetaSharkPlugin.Instance;
            Assert.IsNotNull(plugin);

            if (plugin!.Configuration != null)
            {
                return;
            }

            ReplacePluginConfiguration(new PluginConfiguration());
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

        private sealed class StaticJsonTcpServer : IDisposable
        {
            private readonly string responseBody;
            private readonly TcpListener listener;
            private readonly CancellationTokenSource cancellationTokenSource = new();
            private readonly Task serveTask;

            public StaticJsonTcpServer(string responseBody)
            {
                this.responseBody = responseBody;
                this.listener = new TcpListener(IPAddress.Loopback, 0);
                this.listener.Start();

                var port = ((IPEndPoint)this.listener.LocalEndpoint).Port;
                this.BaseUrl = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
                this.serveTask = Task.Run(this.ServeOnceAsync);
            }

            public string BaseUrl { get; }

            public string? RequestTarget { get; private set; }

            public void Dispose()
            {
                this.cancellationTokenSource.Cancel();
                this.listener.Stop();

                try
                {
                    this.serveTask.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }

                this.cancellationTokenSource.Dispose();
            }

            private async Task ServeOnceAsync()
            {
                using var client = await this.listener.AcceptTcpClientAsync(this.cancellationTokenSource.Token).ConfigureAwait(false);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);

                this.RequestTarget = await ReadRequestTargetAsync(reader).ConfigureAwait(false);

                var bodyBytes = Encoding.UTF8.GetBytes(this.responseBody);
                var headerBytes = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n"
                    + "Content-Type: application/json\r\n"
                    + $"Content-Length: {bodyBytes.Length.ToString(CultureInfo.InvariantCulture)}\r\n"
                    + "Connection: close\r\n\r\n");

                await stream.WriteAsync(headerBytes, this.cancellationTokenSource.Token).ConfigureAwait(false);
                await stream.WriteAsync(bodyBytes, this.cancellationTokenSource.Token).ConfigureAwait(false);
                await stream.FlushAsync(this.cancellationTokenSource.Token).ConfigureAwait(false);
            }

            private static async Task<string?> ReadRequestTargetAsync(StreamReader reader)
            {
                var requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
                while (true)
                {
                    var headerLine = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (string.IsNullOrEmpty(headerLine))
                    {
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    return null;
                }

                var parts = requestLine.Split(' ');
                return parts.Length >= 2 ? parts[1] : requestLine;
            }
        }

        private const string EpisodeImagesJson = """
            {
              "id": 2,
              "stills": [
                {
                  "aspect_ratio": 1.778,
                  "file_path": "/AgVyylbwMmSUW9Fp3wWKEPGT5vH.jpg",
                  "height": 1080,
                  "iso_639_1": "zh",
                  "vote_average": 5.172,
                  "vote_count": 15,
                  "width": 1920
                }
              ]
            }
            """;
    }
}
