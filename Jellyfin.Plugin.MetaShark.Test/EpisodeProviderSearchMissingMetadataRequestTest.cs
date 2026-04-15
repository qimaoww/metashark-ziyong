using Jellyfin.Plugin.MetaShark;
using Jellyfin.Plugin.MetaShark.Api;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Providers;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using System.Reflection;
using TMDbLib.Objects.TvShows;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class EpisodeProviderSearchMissingMetadataRequestTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-episode-provider-request-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");

        private readonly ILoggerFactory loggerFactory = LoggerFactory.Create(builder => { });

        [TestCleanup]
        public void Cleanup()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = false;
        }

        [DataTestMethod]
        [DataRow("FullRefresh", "false", true)]
        [DataRow("fullrefresh", "FALSE", true)]
        [DataRow("FullRefresh", "true", false)]
        [DataRow("Default", "false", false)]
        [DataRow(null, "false", false)]
        [DataRow("FullRefresh", null, false)]
        public void ShouldRecognizeOnlySearchMissingMetadataRefreshMode(string? metadataRefreshMode, string? replaceAllMetadata, bool expected)
        {
            var result = InvokeIsSearchMissingMetadataRefresh(metadataRefreshMode, replaceAllMetadata);

            Assert.AreEqual(expected, result);
        }

        [TestMethod]
        public async Task GetMetadata_ShouldSaveCandidate_WhenRequestMatchesSearchMissingMode()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;

            using var loggerProvider = new TestLoggerProvider();
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(loggerProvider));
            var storeStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            EpisodeTitleBackfillCandidate? savedCandidate = null;
            storeStub
                .Setup(x => x.Save(It.IsAny<EpisodeTitleBackfillCandidate>()))
                .Callback<EpisodeTitleBackfillCandidate>(candidate => savedCandidate = candidate);

            var info = CreateEpisodeInfo();
            var episodeItem = new Episode
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(episodeItem);

            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = CreateHttpContext("FullRefresh", "false"),
            };

            var tmdbApi = new TmdbApi(loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode
            {
                Name = "  皇后回宫  ",
            });

            using var provider = CreateProvider(libraryManagerStub.Object, httpContextAccessor, tmdbApi, storeStub.Object, loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual("皇后回宫", result.Item!.Name);
            storeStub.Verify(x => x.Save(It.IsAny<EpisodeTitleBackfillCandidate>()), Times.Once);
            libraryManagerStub.Verify(x => x.FindByPath(info.Path, false), Times.AtLeastOnce);
            Assert.IsNotNull(savedCandidate);
            Assert.AreEqual(episodeItem.Id, savedCandidate!.ItemId);
            Assert.AreEqual(info.Path, savedCandidate.ItemPath);
            Assert.AreEqual("第 1 集", savedCandidate.OriginalTitleSnapshot);
            Assert.AreEqual("皇后回宫", savedCandidate.CandidateTitle);
            Assert.AreEqual(TimeSpan.FromMinutes(2), savedCandidate.ExpiresAtUtc - savedCandidate.QueuedAtUtc);
            Assert.AreEqual(savedCandidate.QueuedAtUtc, savedCandidate.NextAttemptAtUtc.AddSeconds(-10));
            Assert.AreEqual(0, savedCandidate.AttemptCount);
            AssertLoggedMessage(loggerProvider, "EpisodeProvider", "CandidateQueued", "metadataRefreshMode=FullRefresh", "replaceAllMetadata=false");
        }

        [TestMethod]
        public async Task GetMetadata_WhenSearchMissingMetadataCandidateQueued_LogsEpisodeTitleBackfillDecisionAtInformation()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;

            using var loggerProvider = new TestLoggerProvider();
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(loggerProvider));
            var storeStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            var info = CreateEpisodeInfo();
            var episodeItem = new Episode
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(episodeItem);

            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = CreateHttpContext("FullRefresh", "false"),
            };

            var tmdbApi = new TmdbApi(loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode
            {
                Name = "皇后回宫",
            });

            using var provider = CreateProvider(libraryManagerStub.Object, httpContextAccessor, tmdbApi, storeStub.Object, loggerFactory);

            _ = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            AssertLoggedMessage(
                loggerProvider,
                "EpisodeProvider",
                "EpisodeTitleBackfillDecision",
                "CandidateQueued",
                $"itemId={episodeItem.Id}",
                $"itemPath={info.Path}",
                "metadataRefreshMode=FullRefresh",
                "replaceAllMetadata=false");
            AssertLoggedMessage(
                loggerProvider,
                "EpisodeProvider",
                "EpisodeTitleBackfillQueued",
                $"itemId={episodeItem.Id}",
                $"itemPath={info.Path}",
                "metadataRefreshMode=FullRefresh",
                "replaceAllMetadata=false");
        }

        [TestMethod]
        public async Task GetMetadata_WhenSearchMissingMetadataCandidateQueued_LogsEpisodeTitleBackfillInputsAtInformation()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;

            using var loggerProvider = new TestLoggerProvider();
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(loggerProvider));
            var storeStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            var info = CreateEpisodeInfo(metadataLanguage: null);
            var episodeItem = new Episode
            {
                Id = Guid.NewGuid(),
                Name = info.Name,
                Path = info.Path,
                PreferredMetadataLanguage = "zh-CN",
            };
            var seasonItem = new Season
            {
                Path = "/library/tv/series-a/Season 01",
                PreferredMetadataLanguage = "ja-JP",
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(episodeItem);
            libraryManagerStub
                .Setup(x => x.FindByPath(seasonItem.Path, true))
                .Returns(seasonItem);

            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = CreateHttpContext("FullRefresh", "false"),
            };

            var tmdbApi = new TmdbApi(loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode
            {
                Name = "皇后回宫",
            });

            using var provider = CreateProvider(libraryManagerStub.Object, httpContextAccessor, tmdbApi, storeStub.Object, loggerFactory);

            _ = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(
                loggerProvider.Messages.Any(message => message.LogLevel == LogLevel.Information
                    && message.Category.Contains("EpisodeProvider", StringComparison.Ordinal)
                    && message.Message.Contains("EpisodeTitleBackfillInputs", StringComparison.Ordinal)
                    && message.Message.Contains("lookupLanguage=", StringComparison.Ordinal)
                    && message.Message.Contains("titleMetadataLanguage=zh-CN", StringComparison.Ordinal)
                    && message.Message.Contains("episodePreferredLanguage=zh-CN", StringComparison.Ordinal)
                    && message.Message.Contains("seriesPreferredLanguage=", StringComparison.Ordinal)
                    && message.Message.Contains("seasonPreferredLanguage=ja-JP", StringComparison.Ordinal)
                    && message.Message.Contains("detailsTitle=皇后回宫", StringComparison.Ordinal)
                    && message.Message.Contains("translationTitle=", StringComparison.Ordinal)
                    && message.Message.Contains("effectiveProviderTitle=皇后回宫", StringComparison.Ordinal)
                    && message.Message.Contains("isSearchMissingMetadataRequest=True", StringComparison.Ordinal)),
                "目标 backfill 链路必须输出一条 EpisodeTitleBackfillInputs Information 日志，并带上 title decision 关键输入。");
        }

        [TestMethod]
        public async Task GetMetadata_WhenHttpContextMissingButCurrentEpisodeHasDefaultTitle_QueuesCandidateWithFallback()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;

            using var loggerProvider = new TestLoggerProvider();
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(loggerProvider));
            var storeStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            EpisodeTitleBackfillCandidate? savedCandidate = null;
            storeStub
                .Setup(x => x.Save(It.IsAny<EpisodeTitleBackfillCandidate>()))
                .Callback<EpisodeTitleBackfillCandidate>(candidate => savedCandidate = candidate);

            var info = CreateEpisodeInfo();
            var episodeItem = new Episode
            {
                Id = Guid.NewGuid(),
                Name = info.Name,
                Path = info.Path,
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(episodeItem);

            var tmdbApi = new TmdbApi(loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode
            {
                Name = "皇后回宫",
            });

            using var provider = CreateProvider(libraryManagerStub.Object, new HttpContextAccessor(), tmdbApi, storeStub.Object, loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual("皇后回宫", result.Item!.Name);
            Assert.IsNotNull(savedCandidate, "缺少 HttpContext 时，只要当前宿主标题仍是 Jellyfin 默认占位值，也应按 live fallback 继续入队 candidate。");
            Assert.AreEqual(episodeItem.Id, savedCandidate!.ItemId);
            Assert.AreEqual(info.Path, savedCandidate.ItemPath);
            AssertLoggedMessage(
                loggerProvider,
                "EpisodeProvider",
                "EpisodeTitleBackfillDecision",
                "CandidateQueued",
                $"itemId={episodeItem.Id}",
                $"itemPath={info.Path}");
            AssertLoggedMessage(
                loggerProvider,
                "EpisodeProvider",
                "EpisodeTitleBackfillQueued",
                $"itemId={episodeItem.Id}",
                $"itemPath={info.Path}");
        }

        [TestMethod]
        public async Task GetMetadata_WhenLookupLanguageMissingButEpisodePrefersZhCn_QueuesCandidate()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;

            using var loggerProvider = new TestLoggerProvider();
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(loggerProvider));
            var storeStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            EpisodeTitleBackfillCandidate? savedCandidate = null;
            storeStub
                .Setup(x => x.Save(It.IsAny<EpisodeTitleBackfillCandidate>()))
                .Callback<EpisodeTitleBackfillCandidate>(candidate => savedCandidate = candidate);

            var info = CreateEpisodeInfo(metadataLanguage: null);
            var episodeItem = new Episode
            {
                Id = Guid.NewGuid(),
                Name = info.Name,
                Path = info.Path,
                PreferredMetadataLanguage = "zh-CN",
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(episodeItem);

            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = CreateHttpContext("FullRefresh", "false"),
            };

            var tmdbApi = new TmdbApi(loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode { Name = "皇后回宫" });

            using var provider = CreateProvider(libraryManagerStub.Object, httpContextAccessor, tmdbApi, storeStub.Object, loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual("皇后回宫", result.Item!.Name);
            Assert.IsNotNull(savedCandidate, "当 lookup language 缺失但当前 Episode 偏好 zh-CN 时，strict zh-CN 标题逻辑仍应允许入队 candidate。");
            Assert.AreEqual("皇后回宫", savedCandidate!.CandidateTitle);
            AssertLoggedMessage(loggerProvider, "EpisodeProvider", "CandidateQueued", "metadataRefreshMode=FullRefresh", "replaceAllMetadata=false");
        }

        [TestMethod]
        public async Task GetMetadata_WhenLookupLanguageMissingButEpisodePrefersZhCn_UsesTitleMetadataLanguageForEpisodeDetailsLookup()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;

            using var loggerProvider = new TestLoggerProvider();
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(loggerProvider));
            var storeStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            EpisodeTitleBackfillCandidate? savedCandidate = null;
            storeStub
                .Setup(x => x.Save(It.IsAny<EpisodeTitleBackfillCandidate>()))
                .Callback<EpisodeTitleBackfillCandidate>(candidate => savedCandidate = candidate);

            var info = CreateEpisodeInfo(metadataLanguage: null);
            var episodeItem = new Episode
            {
                Id = Guid.NewGuid(),
                Name = info.Name,
                Path = info.Path,
                PreferredMetadataLanguage = "zh-CN",
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(episodeItem);

            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = CreateHttpContext("FullRefresh", "false"),
            };

            var tmdbApi = new TmdbApi(loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, string.Empty, string.Empty, new TvEpisode { Name = "第 1 集" });
            SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode { Name = "皇后回宫" });

            using var provider = CreateProvider(libraryManagerStub.Object, httpContextAccessor, tmdbApi, storeStub.Object, loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual("皇后回宫", result.Item!.Name, "当 lookup language 为空但 titleMetadataLanguage 可用时，TMDB episode details 查询应命中 titleMetadataLanguage 对应的缓存键。");
            Assert.IsNotNull(savedCandidate, "命中 zh-CN episode details 后，provider 仍应正常入队 candidate。");
            Assert.AreEqual("皇后回宫", savedCandidate!.CandidateTitle);
        }

        [TestMethod]
        public async Task GetMetadata_WhenLookupLanguagePresent_DoesNotFallbackToEpisodePreferredLanguage()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;

            using var loggerProvider = new TestLoggerProvider();
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(loggerProvider));
            var storeStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            var info = CreateEpisodeInfo(metadataLanguage: "en");
            var episodeItem = new Episode
            {
                Id = Guid.NewGuid(),
                Name = info.Name,
                Path = info.Path,
                PreferredMetadataLanguage = "zh-CN",
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(episodeItem);

            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = CreateHttpContext("FullRefresh", "false"),
            };

            var tmdbApi = new TmdbApi(loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "en", "en", new TvEpisode { Name = "皇后回宫" });

            using var provider = CreateProvider(libraryManagerStub.Object, httpContextAccessor, tmdbApi, storeStub.Object, loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual("第 1 集", result.Item!.Name, "显式 lookup language 存在时，provider 不应退回当前 Episode 的 zh-CN 偏好语言。");
            storeStub.Verify(x => x.Save(It.IsAny<EpisodeTitleBackfillCandidate>()), Times.Never);
            AssertLoggedMessage(loggerProvider, "EpisodeProvider", "ResolvedTitleSameAsOriginal", "metadataRefreshMode=FullRefresh", "replaceAllMetadata=false");
        }

        [TestMethod]
        public async Task GetMetadata_WhenLookupLanguageIsBareZhAndProviderTitleIsHumanZhCn_QueuesCandidate()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;

            using var loggerProvider = new TestLoggerProvider();
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(loggerProvider));
            var storeStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            EpisodeTitleBackfillCandidate? savedCandidate = null;
            storeStub
                .Setup(x => x.Save(It.IsAny<EpisodeTitleBackfillCandidate>()))
                .Callback<EpisodeTitleBackfillCandidate>(candidate => savedCandidate = candidate);

            var info = CreateEpisodeInfo(metadataLanguage: "zh");
            var episodeItem = new Episode
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(episodeItem);

            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = CreateHttpContext("FullRefresh", "false"),
            };

            var tmdbApi = new TmdbApi(loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh", "zh", new TvEpisode { Name = "皇后回宫" });

            using var provider = CreateProvider(libraryManagerStub.Object, httpContextAccessor, tmdbApi, storeStub.Object, loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual("皇后回宫", result.Item!.Name);
            Assert.IsNotNull(savedCandidate, "当 lookup language 为 bare zh 且标题文本已满足现有 strict zh-CN 文本门禁时，provider 应继续入队 candidate。");
            Assert.AreEqual("皇后回宫", savedCandidate!.CandidateTitle);
        }

        [TestMethod]
        public async Task GetMetadata_WhenEpisodeDetailsTitleIsGenericAndZhCnTranslationIsNonGeneric_QueuesCandidate()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;

            using var loggerProvider = new TestLoggerProvider();
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(loggerProvider));
            var storeStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            EpisodeTitleBackfillCandidate? savedCandidate = null;
            storeStub
                .Setup(x => x.Save(It.IsAny<EpisodeTitleBackfillCandidate>()))
                .Callback<EpisodeTitleBackfillCandidate>(candidate => savedCandidate = candidate);

            var info = CreateEpisodeInfo();
            var episodeItem = new Episode
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(episodeItem);

            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = CreateHttpContext("FullRefresh", "false"),
            };

            var tmdbApi = new TmdbApi(loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode { Name = "第 1 集" });
            SeedEpisodeTranslationTitle(tmdbApi, 123, 1, 1, "zh-CN", "皇后回宫");

            using var provider = CreateProvider(libraryManagerStub.Object, httpContextAccessor, tmdbApi, storeStub.Object, loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual("皇后回宫", result.Item!.Name);
            Assert.IsNotNull(savedCandidate, "当 details.name 只是 generic 编号标题，而 zh-CN translation 给出有效标题时，provider 应继续入队 candidate。");
            Assert.AreEqual("皇后回宫", savedCandidate!.CandidateTitle);
            AssertLoggedMessage(loggerProvider, "EpisodeProvider", "CandidateQueued", "metadataRefreshMode=FullRefresh", "replaceAllMetadata=false");
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("   ")]
        [DataRow("第 1 集")]
        [DataRow("Episode 1")]
        public async Task GetMetadata_WhenEpisodeDetailsTitleIsGenericAndZhCnTranslationIsMissingOrGeneric_KeepsDefaultTitle(string? translationTitle)
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;

            using var loggerProvider = new TestLoggerProvider();
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(loggerProvider));
            var storeStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            var info = CreateEpisodeInfo();
            var episodeItem = new Episode
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(episodeItem);

            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = CreateHttpContext("FullRefresh", "false"),
            };

            var tmdbApi = new TmdbApi(loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode { Name = "Episode 1" });
            SeedEpisodeTranslationTitle(tmdbApi, 123, 1, 1, "zh-CN", translationTitle);

            using var provider = CreateProvider(libraryManagerStub.Object, httpContextAccessor, tmdbApi, storeStub.Object, loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual("第 1 集", result.Item!.Name);
            storeStub.Verify(x => x.Save(It.IsAny<EpisodeTitleBackfillCandidate>()), Times.Never);
        }

        [TestMethod]
        public async Task GetMetadata_WhenExplicitLanguageIsNonZhCn_DoesNotUseZhCnTranslation()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;

            using var loggerProvider = new TestLoggerProvider();
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(loggerProvider));
            var storeStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            var info = CreateEpisodeInfo(metadataLanguage: "en");
            var episodeItem = new Episode
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(episodeItem);

            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = CreateHttpContext("FullRefresh", "false"),
            };

            var tmdbApi = new TmdbApi(loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "en", "en", new TvEpisode { Name = "Episode 1" });
            SeedEpisodeTranslationTitle(tmdbApi, 123, 1, 1, "zh-CN", "皇后回宫");

            using var provider = CreateProvider(libraryManagerStub.Object, httpContextAccessor, tmdbApi, storeStub.Object, loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual("第 1 集", result.Item!.Name);
            storeStub.Verify(x => x.Save(It.IsAny<EpisodeTitleBackfillCandidate>()), Times.Never);
            AssertLoggedMessage(loggerProvider, "EpisodeProvider", "ResolvedTitleSameAsOriginal", "metadataRefreshMode=FullRefresh", "replaceAllMetadata=false");
        }

        [TestMethod]
        public async Task GetMetadata_ShouldNotSaveCandidate_WhenNoHttpContext()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;

            var storeStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            var info = CreateEpisodeInfo();
            var episodeItem = new Episode { Id = Guid.NewGuid(), Path = info.Path };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(episodeItem);

            var tmdbApi = new TmdbApi(this.loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode { Name = "皇后回宫" });

            using var provider = CreateProvider(libraryManagerStub.Object, new HttpContextAccessor(), tmdbApi, storeStub.Object);

            _ = await provider.GetMetadata(info, CancellationToken.None);

            storeStub.Verify(x => x.Save(It.IsAny<EpisodeTitleBackfillCandidate>()), Times.Never);
        }

        [TestMethod]
        public async Task GetMetadata_ShouldNotSaveCandidate_WhenEpisodeItemIsMissing()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;

            var storeStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            var info = CreateEpisodeInfo();
            var libraryManagerStub = new Mock<ILibraryManager>();
            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = CreateHttpContext("FullRefresh", "false"),
            };

            var tmdbApi = new TmdbApi(this.loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode { Name = "皇后回宫" });

            using var provider = CreateProvider(libraryManagerStub.Object, httpContextAccessor, tmdbApi, storeStub.Object);

            _ = await provider.GetMetadata(info, CancellationToken.None);

            storeStub.Verify(x => x.Save(It.IsAny<EpisodeTitleBackfillCandidate>()), Times.Never);
        }

        [TestMethod]
        public async Task GetMetadata_ShouldNotSaveCandidate_WhenEpisodeItemIdIsEmpty()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;

            var storeStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            var info = CreateEpisodeInfo();
            var episodeItem = new Episode { Id = Guid.Empty, Path = info.Path };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(episodeItem);

            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = CreateHttpContext("FullRefresh", "false"),
            };

            var tmdbApi = new TmdbApi(this.loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode { Name = "皇后回宫" });

            using var provider = CreateProvider(libraryManagerStub.Object, httpContextAccessor, tmdbApi, storeStub.Object);

            _ = await provider.GetMetadata(info, CancellationToken.None);

            storeStub.Verify(x => x.Save(It.IsAny<EpisodeTitleBackfillCandidate>()), Times.Never);
        }

        [TestMethod]
        public async Task GetMetadata_ShouldNotSaveCandidate_WhenRefreshModeIsNotSearchMissingMetadata()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;

            using var loggerProvider = new TestLoggerProvider();
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(loggerProvider));
            var storeStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            var info = CreateEpisodeInfo();
            var episodeItem = new Episode { Id = Guid.NewGuid(), Path = info.Path };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(episodeItem);

            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = CreateHttpContext("FullRefresh", "true"),
            };

            var tmdbApi = new TmdbApi(loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode { Name = "皇后回宫" });

            using var provider = CreateProvider(libraryManagerStub.Object, httpContextAccessor, tmdbApi, storeStub.Object, loggerFactory);

            _ = await provider.GetMetadata(info, CancellationToken.None);

            storeStub.Verify(x => x.Save(It.IsAny<EpisodeTitleBackfillCandidate>()), Times.Never);
            AssertLoggedMessage(loggerProvider, "EpisodeProvider", "RequestNotSearchMissingMetadata", "metadataRefreshMode=FullRefresh", "replaceAllMetadata=true");
        }

        [TestMethod]
        public async Task GetMetadata_ShouldNotSaveCandidate_WhenStrictZhCnRejected()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;

            using var loggerProvider = new TestLoggerProvider();
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(loggerProvider));
            var storeStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            var info = CreateEpisodeInfo();
            var episodeItem = new Episode
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(episodeItem);

            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = CreateHttpContext("FullRefresh", "false"),
            };

            var tmdbApi = new TmdbApi(loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode { Name = "皇后回宮" });

            using var provider = CreateProvider(libraryManagerStub.Object, httpContextAccessor, tmdbApi, storeStub.Object, loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual("第 1 集", result.Item!.Name);
            storeStub.Verify(x => x.Save(It.IsAny<EpisodeTitleBackfillCandidate>()), Times.Never);
            AssertLoggedMessage(loggerProvider, "EpisodeProvider", "StrictZhCnRejected", "metadataRefreshMode=FullRefresh", "replaceAllMetadata=false");
        }

        [TestMethod]
        public async Task GetMetadata_WhenLookupLanguageIsBareZhButProviderTitleIsDisallowed_KeepsDefaultTitle()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;

            using var loggerProvider = new TestLoggerProvider();
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(loggerProvider));
            var storeStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            var info = CreateEpisodeInfo(metadataLanguage: "zh");
            var episodeItem = new Episode
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(episodeItem);

            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = CreateHttpContext("FullRefresh", "false"),
            };

            var tmdbApi = new TmdbApi(loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh", "zh", new TvEpisode { Name = "皇后回宮" });

            using var provider = CreateProvider(libraryManagerStub.Object, httpContextAccessor, tmdbApi, storeStub.Object, loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual("第 1 集", result.Item!.Name);
            storeStub.Verify(x => x.Save(It.IsAny<EpisodeTitleBackfillCandidate>()), Times.Never);
        }

        [TestMethod]
        public async Task GetMetadata_ShouldLogResolvedTitleEmpty_WhenProviderTitleIsWhitespace()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = true;

            using var loggerProvider = new TestLoggerProvider();
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(loggerProvider));
            var storeStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            var info = CreateEpisodeInfo();
            var episodeItem = new Episode
            {
                Id = Guid.NewGuid(),
                Path = info.Path,
            };

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.FindByPath(info.Path, false))
                .Returns(episodeItem);

            var httpContextAccessor = new HttpContextAccessor
            {
                HttpContext = CreateHttpContext("FullRefresh", "false"),
            };

            var tmdbApi = new TmdbApi(loggerFactory);
            SeedEpisode(tmdbApi, 123, 1, 1, "zh-CN", "zh-CN", new TvEpisode { Name = "   " });

            using var provider = CreateProvider(libraryManagerStub.Object, httpContextAccessor, tmdbApi, storeStub.Object, loggerFactory);

            var result = await provider.GetMetadata(info, CancellationToken.None);

            Assert.IsNotNull(result.Item);
            Assert.AreEqual("第 1 集", result.Item!.Name);
            storeStub.Verify(x => x.Save(It.IsAny<EpisodeTitleBackfillCandidate>()), Times.Never);
            AssertLoggedMessage(loggerProvider, "EpisodeProvider", "ResolvedTitleEmpty", "metadataRefreshMode=FullRefresh", "replaceAllMetadata=false");
        }

        private static EpisodeProvider CreateProvider(
            ILibraryManager libraryManager,
            IHttpContextAccessor httpContextAccessor,
            TmdbApi tmdbApi,
            IEpisodeTitleBackfillCandidateStore store,
            ILoggerFactory? loggerFactory = null)
        {
            var constructor = typeof(EpisodeProvider).GetConstructor(new[]
            {
                typeof(IHttpClientFactory),
                typeof(ILoggerFactory),
                typeof(ILibraryManager),
                typeof(IHttpContextAccessor),
                typeof(DoubanApi),
                typeof(TmdbApi),
                typeof(OmdbApi),
                typeof(ImdbApi),
                typeof(TvdbApi),
                typeof(IEpisodeTitleBackfillCandidateStore),
            });

            Assert.IsNotNull(constructor, "EpisodeProvider 尚未注入 IEpisodeTitleBackfillCandidateStore");

            loggerFactory ??= LoggerFactory.Create(builder => { });
            return (EpisodeProvider)constructor!.Invoke(new object[]
            {
                new DefaultHttpClientFactory(),
                loggerFactory,
                libraryManager,
                httpContextAccessor,
                new DoubanApi(loggerFactory),
                tmdbApi,
                new OmdbApi(loggerFactory),
                new ImdbApi(loggerFactory),
                new TvdbApi(loggerFactory),
                store,
            });
        }

        private static void AssertLoggedMessage(TestLoggerProvider loggerProvider, string categoryFragment, params string[] fragments)
        {
            var matches = loggerProvider.Messages.Any(message => message.Category.Contains(categoryFragment, StringComparison.Ordinal)
                && fragments.All(fragment => message.Message.Contains(fragment, StringComparison.Ordinal)));
            Assert.IsTrue(matches, $"Expected log containing fragments: {string.Join(", ", fragments)}");
        }

        private static DefaultHttpContext CreateHttpContext(string metadataRefreshMode, string replaceAllMetadata)
        {
            var context = new DefaultHttpContext();
            context.Request.QueryString = new QueryString($"?metadataRefreshMode={metadataRefreshMode}&replaceAllMetadata={replaceAllMetadata}");
            return context;
        }

        private static EpisodeInfo CreateEpisodeInfo(string? metadataLanguage = "zh-CN")
        {
            return new EpisodeInfo
            {
                Name = "第 1 集",
                Path = "/library/tv/series-a/Season 01/episode-01.mkv",
                MetadataLanguage = metadataLanguage,
                ParentIndexNumber = 1,
                IndexNumber = 1,
                SeriesDisplayOrder = string.Empty,
                SeriesProviderIds = new Dictionary<string, string>
                {
                    [MetadataProvider.Tmdb.ToString()] = "123",
                },
            };
        }

        private static bool InvokeIsSearchMissingMetadataRefresh(string? metadataRefreshMode, string? replaceAllMetadata)
        {
            var method = typeof(EpisodeProvider).GetMethod(
                "IsSearchMissingMetadataRefresh",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            Assert.IsNotNull(method, "EpisodeProvider.IsSearchMissingMetadataRefresh 未定义");

            return (bool)method!.Invoke(null, new object?[] { metadataRefreshMode, replaceAllMetadata })!;
        }

        private static void SeedEpisode(TmdbApi tmdbApi, int seriesTmdbId, int seasonNumber, int episodeNumber, string language, string imageLanguages, TvEpisode episode)
        {
            var cacheField = typeof(TmdbApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(cacheField, "TmdbApi.memoryCache 未找到");

            var cache = cacheField!.GetValue(tmdbApi) as MemoryCache;
            Assert.IsNotNull(cache, "TmdbApi.memoryCache 不是有效的 MemoryCache");

            var key = $"episode-{seriesTmdbId}-s{seasonNumber}e{episodeNumber}-{language}-{imageLanguages}";
            cache!.Set(key, episode);
        }

        private static void SeedEpisodeTranslationTitle(TmdbApi tmdbApi, int seriesTmdbId, int seasonNumber, int episodeNumber, string language, string? title)
        {
            var cacheField = typeof(TmdbApi).GetField("memoryCache", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(cacheField, "TmdbApi.memoryCache 未找到");

            var cache = cacheField!.GetValue(tmdbApi) as MemoryCache;
            Assert.IsNotNull(cache, "TmdbApi.memoryCache 不是有效的 MemoryCache");

            var key = $"episode-translation-title-{seriesTmdbId}-s{seasonNumber}e{episodeNumber}-{language}";
            cache!.Set(key, title);
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

            var configuration = new PluginConfiguration();
            var currentType = plugin.GetType();
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

            Assert.Fail("Could not initialize MetaSharkPlugin configuration for tests.");
        }

        private sealed class TestLoggerProvider : ILoggerProvider
        {
            private readonly List<LoggedMessage> messages = new List<LoggedMessage>();

            public IReadOnlyList<LoggedMessage> Messages => this.messages;

            public ILogger CreateLogger(string categoryName)
            {
                return new TestLogger(categoryName, this.messages);
            }

            public void Dispose()
            {
            }
        }

        private sealed class TestLogger : ILogger
        {
            private readonly string categoryName;
            private readonly List<LoggedMessage> messages;

            public TestLogger(string categoryName, List<LoggedMessage> messages)
            {
                this.categoryName = categoryName;
                this.messages = messages;
            }

            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull
            {
                return NullScope.Instance;
            }

            public bool IsEnabled(LogLevel logLevel)
            {
                return true;
            }

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                this.messages.Add(new LoggedMessage(this.categoryName, logLevel, eventId, formatter(state, exception)));
            }
        }

        private sealed record LoggedMessage(string Category, LogLevel LogLevel, EventId EventId, string Message);

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new NullScope();

            public void Dispose()
            {
            }
        }
    }
}
