using Jellyfin.Plugin.MetaShark;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class EpisodeTitleBackfillPostProcessServiceTest
    {
        private static readonly string PluginTestRootPath = Path.Combine(Path.GetTempPath(), "metashark-episode-title-backfill-postprocess-tests");
        private static readonly string PluginsPath = Path.Combine(PluginTestRootPath, "plugins");
        private static readonly string PluginConfigurationsPath = Path.Combine(PluginTestRootPath, "configurations");

        [TestCleanup]
        public void Cleanup()
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = false;
        }

        [TestMethod]
        public async Task ProcessUpdatedItemAsync_MetadataImportWithValidCandidate_SavesAndUpdatesEpisodeName()
        {
            SetFeatureEnabled(true);

            var episode = CreateEpisode("  第 1 集  ");
            var candidate = CreateCandidate(episode.Id, "第 1 集", "皇后回宫");
            var candidateStoreStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            candidateStoreStub
                .Setup(x => x.Consume(episode.Id, It.IsAny<DateTimeOffset>()))
                .Returns(candidate);

            var persistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            persistenceStub
                .Setup(x => x.SaveAsync(episode, CancellationToken.None))
                .Returns(Task.CompletedTask);

            var service = CreateService(candidateStoreStub.Object, persistenceStub.Object, out var loggerStub);

            await service.ProcessUpdatedItemAsync(CreateUpdate(episode, ItemUpdateType.MetadataImport), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("皇后回宫", episode.Name);
            candidateStoreStub.Verify(x => x.Consume(episode.Id, It.IsAny<DateTimeOffset>()), Times.Once);
            persistenceStub.Verify(x => x.SaveAsync(episode, CancellationToken.None), Times.Once);
            VerifyLoggedMessage(loggerStub, LogLevel.Information, episode.Id.ToString(), "第 1 集", "皇后回宫", "MetadataImport");
        }

        [TestMethod]
        public async Task ProcessUpdatedItemAsync_MetadataImportAndMetadataDownloadWithValidCandidate_SavesOnce()
        {
            SetFeatureEnabled(true);

            var episode = CreateEpisode("第 1 集");
            var candidate = CreateCandidate(episode.Id);
            var candidateStoreStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            candidateStoreStub
                .Setup(x => x.Consume(episode.Id, It.IsAny<DateTimeOffset>()))
                .Returns(candidate);

            var persistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            persistenceStub
                .Setup(x => x.SaveAsync(episode, CancellationToken.None))
                .Returns(Task.CompletedTask);

            var service = CreateService(candidateStoreStub.Object, persistenceStub.Object, out _);

            await service.ProcessUpdatedItemAsync(
                CreateUpdate(episode, ItemUpdateType.MetadataImport | ItemUpdateType.MetadataDownload),
                CancellationToken.None).ConfigureAwait(false);

            persistenceStub.Verify(x => x.SaveAsync(episode, CancellationToken.None), Times.Once);
        }

        [DataTestMethod]
        [DataRow(ItemUpdateType.MetadataEdit)]
        [DataRow(ItemUpdateType.ImageUpdate)]
        public async Task ProcessUpdatedItemAsync_NonMetadataImportReason_DoesNotSave(ItemUpdateType updateReason)
        {
            SetFeatureEnabled(true);

            var episode = CreateEpisode();
            var candidateStoreStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            var persistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            var service = CreateService(candidateStoreStub.Object, persistenceStub.Object, out var loggerStub);

            await service.ProcessUpdatedItemAsync(CreateUpdate(episode, updateReason), CancellationToken.None).ConfigureAwait(false);

            candidateStoreStub.Verify(x => x.Consume(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>()), Times.Never);
            persistenceStub.Verify(x => x.SaveAsync(It.IsAny<Episode>(), It.IsAny<CancellationToken>()), Times.Never);
            VerifyLoggedMessage(loggerStub, LogLevel.Debug, episode.Id.ToString(), episode.Name!, "candidateTitle=", updateReason.ToString());
        }

        [TestMethod]
        public async Task ProcessUpdatedItemAsync_FeatureDisabled_RemovesCandidateAndDoesNotSave()
        {
            SetFeatureEnabled(false);

            var episode = CreateEpisode();
            var candidateStoreStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            var persistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            var service = CreateService(candidateStoreStub.Object, persistenceStub.Object, out var loggerStub);

            await service.ProcessUpdatedItemAsync(CreateUpdate(episode, ItemUpdateType.MetadataImport), CancellationToken.None).ConfigureAwait(false);

            candidateStoreStub.Verify(x => x.Remove(episode.Id), Times.Once);
            candidateStoreStub.Verify(x => x.Consume(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>()), Times.Never);
            persistenceStub.Verify(x => x.SaveAsync(It.IsAny<Episode>(), It.IsAny<CancellationToken>()), Times.Never);
            VerifyLoggedMessage(loggerStub, LogLevel.Debug, episode.Id.ToString(), "disabled", episode.Name!, "candidateTitle=", "MetadataImport");
        }

        [TestMethod]
        public async Task ProcessUpdatedItemAsync_NoCandidateReturned_DoesNotSave()
        {
            SetFeatureEnabled(true);

            var episode = CreateEpisode();
            var candidateStoreStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            candidateStoreStub
                .Setup(x => x.Consume(episode.Id, It.IsAny<DateTimeOffset>()))
                .Returns((EpisodeTitleBackfillCandidate?)null);

            var persistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            var service = CreateService(candidateStoreStub.Object, persistenceStub.Object, out var loggerStub);

            await service.ProcessUpdatedItemAsync(CreateUpdate(episode, ItemUpdateType.MetadataImport), CancellationToken.None).ConfigureAwait(false);

            candidateStoreStub.Verify(x => x.Consume(episode.Id, It.IsAny<DateTimeOffset>()), Times.Once);
            persistenceStub.Verify(x => x.SaveAsync(It.IsAny<Episode>(), It.IsAny<CancellationToken>()), Times.Never);
            VerifyLoggedMessage(loggerStub, LogLevel.Debug, episode.Id.ToString(), "No candidate", episode.Name!, "candidateTitle=", "MetadataImport");
        }

        [DataTestMethod]
        [DataRow(true, false)]
        [DataRow(false, true)]
        public async Task ProcessUpdatedItemAsync_LockedEpisodeOrNameField_DoesNotSave(bool isLocked, bool lockNameField)
        {
            SetFeatureEnabled(true);

            var episode = CreateEpisode();
            episode.IsLocked = isLocked;
            if (lockNameField)
            {
                episode.LockedFields = new[] { MetadataField.Name };
            }

            var candidateStoreStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            candidateStoreStub
                .Setup(x => x.Consume(episode.Id, It.IsAny<DateTimeOffset>()))
                .Returns(CreateCandidate(episode.Id));

            var persistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            var service = CreateService(candidateStoreStub.Object, persistenceStub.Object, out var loggerStub);

            await service.ProcessUpdatedItemAsync(CreateUpdate(episode, ItemUpdateType.MetadataImport), CancellationToken.None).ConfigureAwait(false);

            candidateStoreStub.Verify(x => x.Consume(episode.Id, It.IsAny<DateTimeOffset>()), Times.Once);
            persistenceStub.Verify(x => x.SaveAsync(It.IsAny<Episode>(), It.IsAny<CancellationToken>()), Times.Never);
            VerifyLoggedMessage(loggerStub, LogLevel.Debug, episode.Id.ToString(), "locked", "第 1 集", "皇后回宫", "MetadataImport");
        }

        [TestMethod]
        public async Task ProcessUpdatedItemAsync_CurrentTitleSnapshotMismatch_DoesNotSave()
        {
            SetFeatureEnabled(true);

            var episode = CreateEpisode("  第 1 集  ");
            var candidateStoreStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            candidateStoreStub
                .Setup(x => x.Consume(episode.Id, It.IsAny<DateTimeOffset>()))
                .Returns(CreateCandidate(episode.Id, "  第 2 集  ", "皇后回宫"));

            var persistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            var service = CreateService(candidateStoreStub.Object, persistenceStub.Object, out var loggerStub);

            await service.ProcessUpdatedItemAsync(CreateUpdate(episode, ItemUpdateType.MetadataImport), CancellationToken.None).ConfigureAwait(false);

            persistenceStub.Verify(x => x.SaveAsync(It.IsAny<Episode>(), It.IsAny<CancellationToken>()), Times.Never);
            VerifyLoggedMessage(loggerStub, LogLevel.Debug, episode.Id.ToString(), "第 1 集", "第 2 集", "皇后回宫", "MetadataImport");
        }

        [TestMethod]
        public async Task ProcessUpdatedItemAsync_CurrentTitleIsNotDefaultJellyfinEpisodeTitle_DoesNotSave()
        {
            SetFeatureEnabled(true);

            var episode = CreateEpisode("  重逢  ");
            var candidateStoreStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            candidateStoreStub
                .Setup(x => x.Consume(episode.Id, It.IsAny<DateTimeOffset>()))
                .Returns(CreateCandidate(episode.Id, " 重逢 ", "皇后回宫"));

            var persistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            var service = CreateService(candidateStoreStub.Object, persistenceStub.Object, out var loggerStub);

            await service.ProcessUpdatedItemAsync(CreateUpdate(episode, ItemUpdateType.MetadataImport), CancellationToken.None).ConfigureAwait(false);

            persistenceStub.Verify(x => x.SaveAsync(It.IsAny<Episode>(), It.IsAny<CancellationToken>()), Times.Never);
            VerifyLoggedMessage(loggerStub, LogLevel.Debug, episode.Id.ToString(), "重逢", "皇后回宫", "MetadataImport");
        }

        [TestMethod]
        public async Task ProcessUpdatedItemAsync_CurrentTitleAlreadyMatchesCandidateAfterTrim_DoesNotSave()
        {
            SetFeatureEnabled(true);

            var episode = CreateEpisode("  第 1 集  ");
            var candidateStoreStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            candidateStoreStub
                .Setup(x => x.Consume(episode.Id, It.IsAny<DateTimeOffset>()))
                .Returns(CreateCandidate(episode.Id, "第 1 集", "  第 1 集  "));

            var persistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            var service = CreateService(candidateStoreStub.Object, persistenceStub.Object, out var loggerStub);

            await service.ProcessUpdatedItemAsync(CreateUpdate(episode, ItemUpdateType.MetadataImport), CancellationToken.None).ConfigureAwait(false);

            persistenceStub.Verify(x => x.SaveAsync(It.IsAny<Episode>(), It.IsAny<CancellationToken>()), Times.Never);
            VerifyLoggedMessage(loggerStub, LogLevel.Debug, episode.Id.ToString(), "current title 第 1 集", "candidate title 第 1 集", "MetadataImport");
        }

        [TestMethod]
        public async Task ProcessUpdatedItemAsync_ConsumedCandidateIsOnlyAppliedOnceAcrossRepeatedCalls()
        {
            SetFeatureEnabled(true);

            var episodeId = Guid.NewGuid();
            var candidateStore = new InMemoryEpisodeTitleBackfillCandidateStore();
            candidateStore.Save(CreateCandidate(episodeId, "第 1 集", "皇后回宫"));

            var firstEpisode = new Episode
            {
                Id = episodeId,
                Name = "第 1 集",
                Path = "/library/tv/series-a/Season 01/episode-01.mkv",
            };
            var secondEpisode = new Episode
            {
                Id = episodeId,
                Name = "第 1 集",
                Path = "/library/tv/series-a/Season 01/episode-01.mkv",
            };

            var persistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            persistenceStub
                .Setup(x => x.SaveAsync(It.IsAny<Episode>(), CancellationToken.None))
                .Returns(Task.CompletedTask);

            var service = CreateService(candidateStore, persistenceStub.Object, out var loggerStub);

            await service.ProcessUpdatedItemAsync(CreateUpdate(firstEpisode, ItemUpdateType.MetadataImport), CancellationToken.None).ConfigureAwait(false);
            await service.ProcessUpdatedItemAsync(CreateUpdate(secondEpisode, ItemUpdateType.MetadataImport), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("皇后回宫", firstEpisode.Name);
            Assert.AreEqual("第 1 集", secondEpisode.Name);
            persistenceStub.Verify(x => x.SaveAsync(It.IsAny<Episode>(), CancellationToken.None), Times.Once);
            VerifyLoggedMessage(loggerStub, LogLevel.Debug, episodeId.ToString(), "No candidate", "第 1 集", "candidateTitle=", "MetadataImport");
        }

        [TestMethod]
        public async Task ProcessUpdatedItemAsync_NonEpisodeItem_DoesNothing()
        {
            SetFeatureEnabled(true);

            var movie = new MediaBrowser.Controller.Entities.Movies.Movie { Id = Guid.NewGuid(), Name = "Movie A" };
            var candidateStoreStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            var persistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            var service = CreateService(candidateStoreStub.Object, persistenceStub.Object, out _);

            await service.ProcessUpdatedItemAsync(
                new ItemChangeEventArgs { Item = movie, UpdateReason = ItemUpdateType.MetadataImport },
                CancellationToken.None).ConfigureAwait(false);

            candidateStoreStub.Verify(x => x.Remove(It.IsAny<Guid>()), Times.Never);
            candidateStoreStub.Verify(x => x.Consume(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>()), Times.Never);
            persistenceStub.Verify(x => x.SaveAsync(It.IsAny<Episode>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestMethod]
        public async Task ProcessUpdatedItemAsync_EmptyEpisodeId_DoesNothing()
        {
            SetFeatureEnabled(true);

            var episode = CreateEpisode();
            episode.Id = Guid.Empty;
            var candidateStoreStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            var persistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            var service = CreateService(candidateStoreStub.Object, persistenceStub.Object, out _);

            await service.ProcessUpdatedItemAsync(CreateUpdate(episode, ItemUpdateType.MetadataImport), CancellationToken.None).ConfigureAwait(false);

            candidateStoreStub.Verify(x => x.Remove(It.IsAny<Guid>()), Times.Never);
            candidateStoreStub.Verify(x => x.Consume(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>()), Times.Never);
            persistenceStub.Verify(x => x.SaveAsync(It.IsAny<Episode>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        private static EpisodeTitleBackfillPostProcessService CreateService(
            IEpisodeTitleBackfillCandidateStore candidateStore,
            IEpisodeTitleBackfillPersistence persistence,
            out Mock<ILogger<EpisodeTitleBackfillPostProcessService>> loggerStub)
        {
            loggerStub = new Mock<ILogger<EpisodeTitleBackfillPostProcessService>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            return new EpisodeTitleBackfillPostProcessService(candidateStore, persistence, loggerStub.Object);
        }

        private static ItemChangeEventArgs CreateUpdate(Episode episode, ItemUpdateType updateReason)
        {
            return new ItemChangeEventArgs
            {
                Item = episode,
                UpdateReason = updateReason,
            };
        }

        private static Episode CreateEpisode(string name = "第 1 集")
        {
            return new Episode
            {
                Id = Guid.NewGuid(),
                Name = name,
                Path = "/library/tv/series-a/Season 01/episode-01.mkv",
            };
        }

        private static EpisodeTitleBackfillCandidate CreateCandidate(Guid itemId, string originalTitleSnapshot = "第 1 集", string candidateTitle = "皇后回宫")
        {
            return new EpisodeTitleBackfillCandidate
            {
                ItemId = itemId,
                OriginalTitleSnapshot = originalTitleSnapshot,
                CandidateTitle = candidateTitle,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10),
            };
        }

        private static void VerifyLoggedMessage(Mock<ILogger<EpisodeTitleBackfillPostProcessService>> loggerStub, LogLevel level, params string[] fragments)
        {
            loggerStub.Verify(
                x => x.Log(
                    level,
                    It.IsAny<EventId>(),
                    It.Is<It.IsAnyType>((state, _) => fragments.All(fragment => state.ToString()!.Contains(fragment, StringComparison.Ordinal))),
                    It.IsAny<Exception>(),
                    It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
                Times.AtLeastOnce);
        }

        private static void SetFeatureEnabled(bool enabled)
        {
            EnsurePluginInstance();
            MetaSharkPlugin.Instance!.Configuration.EnableSearchMissingMetadataEpisodeTitleBackfill = enabled;
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
                var configurationProperty = currentType.GetProperty("Configuration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (configurationProperty != null
                    && configurationProperty.PropertyType.IsAssignableFrom(typeof(PluginConfiguration))
                    && configurationProperty.SetMethod != null)
                {
                    configurationProperty.SetValue(plugin, configuration);
                    return;
                }

                var configurationField = currentType
                    .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
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
    }
}
