using Jellyfin.Plugin.MetaShark;
using Jellyfin.Plugin.MetaShark.Configuration;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Test.Logging;
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
using System.Collections.Generic;
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
                .Setup(x => x.Peek(episode.Id))
                .Returns(candidate);

            var persistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            persistenceStub
                .Setup(x => x.SaveAsync(episode, CancellationToken.None))
                .Returns(Task.CompletedTask);

            var service = CreateService(candidateStoreStub.Object, persistenceStub.Object, out var loggerStub);

            await service.ProcessUpdatedItemAsync(CreateUpdate(episode, ItemUpdateType.MetadataImport), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("皇后回宫", episode.Name);
            candidateStoreStub.Verify(x => x.Peek(episode.Id), Times.Once);
            candidateStoreStub.Verify(x => x.Remove(episode.Id, episode.Path), Times.Once);
            persistenceStub.Verify(x => x.SaveAsync(episode, CancellationToken.None), Times.Once);
            AssertAppliedLog(loggerStub, episode.Id, episode.Path!, "第 1 集", "皇后回宫", ItemUpdateType.MetadataImport);
        }

        [TestMethod]
        public async Task ProcessUpdatedItemAsync_MetadataImportAndMetadataDownloadWithValidCandidate_SavesOnce()
        {
            SetFeatureEnabled(true);

            var episode = CreateEpisode("第 1 集");
            var candidate = CreateCandidate(episode.Id);
            var candidateStoreStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            candidateStoreStub
                .Setup(x => x.Peek(episode.Id))
                .Returns(candidate);

            var persistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            persistenceStub
                .Setup(x => x.SaveAsync(episode, CancellationToken.None))
                .Returns(Task.CompletedTask);

            var service = CreateService(candidateStoreStub.Object, persistenceStub.Object, out _);

            await service.ProcessUpdatedItemAsync(
                CreateUpdate(episode, ItemUpdateType.MetadataImport | ItemUpdateType.MetadataDownload),
                CancellationToken.None).ConfigureAwait(false);

            candidateStoreStub.Verify(x => x.Peek(episode.Id), Times.Once);
            candidateStoreStub.Verify(x => x.Remove(episode.Id, episode.Path), Times.Once);
            persistenceStub.Verify(x => x.SaveAsync(episode, CancellationToken.None), Times.Once);
        }

        [TestMethod]
        public async Task AppliesCandidate_WhenUpdateReasonIncludesMetadataDownload()
        {
            SetFeatureEnabled(true);

            var episode = CreateEpisode("第 1 集");
            var candidateStore = new InMemoryEpisodeTitleBackfillCandidateStore();
            candidateStore.Save(CreateCandidate(episode.Id, "第 1 集", "皇后回宫"));

            var persistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            persistenceStub
                .Setup(x => x.SaveAsync(episode, CancellationToken.None))
                .Returns(Task.CompletedTask);

            var service = CreateService(candidateStore, persistenceStub.Object, out _);

            await service.ProcessUpdatedItemAsync(CreateUpdate(episode, ItemUpdateType.MetadataDownload), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("皇后回宫", episode.Name);
            persistenceStub.Verify(x => x.SaveAsync(episode, CancellationToken.None), Times.Once);
        }

        [TestMethod]
        public async Task AppliesCandidate_WhenUpdateReasonCombinesMetadataImportAndMetadataDownload()
        {
            SetFeatureEnabled(true);

            var episodeId = Guid.NewGuid();
            var candidateStore = new InMemoryEpisodeTitleBackfillCandidateStore();
            candidateStore.Save(CreateCandidate(episodeId, "第 1 集", "皇后回宫"));

            var failingEpisode = new Episode
            {
                Id = episodeId,
                Name = "第 1 集",
                Path = "/library/tv/series-a/Season 01/episode-01.mkv",
            };

            var retryEpisode = new Episode
            {
                Id = episodeId,
                Name = "第 1 集",
                Path = "/library/tv/series-a/Season 01/episode-01.mkv",
            };

            var failingPersistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            failingPersistenceStub
                .Setup(x => x.SaveAsync(failingEpisode, CancellationToken.None))
                .ThrowsAsync(new InvalidOperationException("persistence boom"));

            var retryPersistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            retryPersistenceStub
                .Setup(x => x.SaveAsync(retryEpisode, CancellationToken.None))
                .Returns(Task.CompletedTask);

            var updateReason = ItemUpdateType.MetadataImport | ItemUpdateType.MetadataDownload;
            var failingService = CreateService(candidateStore, failingPersistenceStub.Object, out _);
            var retryService = CreateService(candidateStore, retryPersistenceStub.Object, out _);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => failingService.ProcessUpdatedItemAsync(CreateUpdate(failingEpisode, updateReason), CancellationToken.None)).ConfigureAwait(false);

            await retryService.ProcessUpdatedItemAsync(CreateUpdate(retryEpisode, updateReason), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("皇后回宫", retryEpisode.Name);
            retryPersistenceStub.Verify(x => x.SaveAsync(retryEpisode, CancellationToken.None), Times.Once);
        }

        [TestMethod]
        public async Task DoesNotApplyCandidate_WhenUpdateReasonIsMetadataEditOnly()
        {
            SetFeatureEnabled(true);

            var episodeId = Guid.NewGuid();
            var candidateStore = new InMemoryEpisodeTitleBackfillCandidateStore();
            candidateStore.Save(CreateCandidate(episodeId, "第 1 集", "皇后回宫"));

            var metadataEditEpisode = new Episode
            {
                Id = episodeId,
                Name = "第 1 集",
                Path = "/library/tv/series-a/Season 01/episode-01.mkv",
            };

            var metadataDownloadEpisode = new Episode
            {
                Id = episodeId,
                Name = "第 1 集",
                Path = "/library/tv/series-a/Season 01/episode-01.mkv",
            };

            var persistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            persistenceStub
                .Setup(x => x.SaveAsync(metadataDownloadEpisode, CancellationToken.None))
                .Returns(Task.CompletedTask);

            var service = CreateService(candidateStore, persistenceStub.Object, out _);

            await service.ProcessUpdatedItemAsync(CreateUpdate(metadataEditEpisode, ItemUpdateType.MetadataEdit), CancellationToken.None).ConfigureAwait(false);
            await service.ProcessUpdatedItemAsync(CreateUpdate(metadataDownloadEpisode, ItemUpdateType.MetadataDownload), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("第 1 集", metadataEditEpisode.Name);
            Assert.AreEqual("皇后回宫", metadataDownloadEpisode.Name);
            persistenceStub.Verify(x => x.SaveAsync(metadataDownloadEpisode, CancellationToken.None), Times.Once);
        }

        [TestMethod]
        public async Task DoesNotRemoveCandidate_WhenUpdateReasonIsRejected()
        {
            SetFeatureEnabled(true);

            var episodeId = Guid.NewGuid();
            var candidateStore = new InMemoryEpisodeTitleBackfillCandidateStore();
            candidateStore.Save(CreateCandidate(episodeId, "第 1 集", "皇后回宫"));

            var rejectedEpisode = new Episode
            {
                Id = episodeId,
                Name = "第 1 集",
                Path = "/library/tv/series-a/Season 01/episode-01.mkv",
            };

            var retryEpisode = new Episode
            {
                Id = episodeId,
                Name = "第 1 集",
                Path = "/library/tv/series-a/Season 01/episode-01.mkv",
            };

            var persistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            persistenceStub
                .Setup(x => x.SaveAsync(retryEpisode, CancellationToken.None))
                .Returns(Task.CompletedTask);

            var service = CreateService(candidateStore, persistenceStub.Object, out _);

            await service.ProcessUpdatedItemAsync(CreateUpdate(rejectedEpisode, ItemUpdateType.ImageUpdate), CancellationToken.None).ConfigureAwait(false);
            await service.ProcessUpdatedItemAsync(CreateUpdate(retryEpisode, ItemUpdateType.MetadataDownload), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("第 1 集", rejectedEpisode.Name);
            Assert.AreEqual("皇后回宫", retryEpisode.Name);
            persistenceStub.Verify(x => x.SaveAsync(retryEpisode, CancellationToken.None), Times.Once);
        }

        [TestMethod]
        public async Task RestoresEpisodeNameAndDoesNotRemoveCandidate_WhenPersistenceThrows()
        {
            SetFeatureEnabled(true);

            var episodeId = Guid.NewGuid();
            var candidateStore = new InMemoryEpisodeTitleBackfillCandidateStore();
            candidateStore.Save(CreateCandidate(episodeId, "第 1 集", "皇后回宫"));

            var episode = new Episode
            {
                Id = episodeId,
                Name = "第 1 集",
                Path = "/library/tv/series-a/Season 01/episode-01.mkv",
            };

            var persistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            persistenceStub
                .SetupSequence(x => x.SaveAsync(episode, CancellationToken.None))
                .ThrowsAsync(new InvalidOperationException("persistence boom"))
                .Returns(Task.CompletedTask);

            var service = CreateService(candidateStore, persistenceStub.Object, out _);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => service.ProcessUpdatedItemAsync(CreateUpdate(episode, ItemUpdateType.MetadataImport), CancellationToken.None)).ConfigureAwait(false);

            Assert.AreEqual("第 1 集", episode.Name);

            await service.ProcessUpdatedItemAsync(CreateUpdate(episode, ItemUpdateType.MetadataImport), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("皇后回宫", episode.Name);
            persistenceStub.Verify(x => x.SaveAsync(episode, CancellationToken.None), Times.Exactly(2));
        }

        [TestMethod]
        public async Task TryApplyAsync_WhenPathFallbackFindsRecreatedEpisode_RebindsAndApplies()
        {
            SetFeatureEnabled(true);

            var originalEpisodeId = Guid.NewGuid();
            var recreatedEpisode = new Episode
            {
                Id = Guid.NewGuid(),
                Name = "第 1 集",
                Path = "/library/tv/series-a/Season 01/episode-01.mkv",
            };

            var candidateStore = new InMemoryEpisodeTitleBackfillCandidateStore();
            candidateStore.Save(CreatePathAwareCandidate(originalEpisodeId, recreatedEpisode.Path, "第 1 集", "皇后回宫"));

            var failingPersistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            failingPersistenceStub
                .Setup(x => x.SaveAsync(recreatedEpisode, CancellationToken.None))
                .ThrowsAsync(new InvalidOperationException("persistence boom"));

            var failingService = CreateService(candidateStore, failingPersistenceStub.Object, out _);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => failingService.ProcessUpdatedItemAsync(CreateUpdate(recreatedEpisode, ItemUpdateType.MetadataDownload), CancellationToken.None)).ConfigureAwait(false);

            Assert.IsNull(candidateStore.Peek(originalEpisodeId), "path fallback 重新绑定后，旧 itemId 不应继续命中 candidate。");
            var reboundCandidate = candidateStore.Peek(recreatedEpisode.Id);
            Assert.IsNotNull(reboundCandidate, "SaveAsync 失败时也应保留已重绑到当前 itemId 的 candidate，避免后续重试丢失工作项。");
            Assert.AreEqual(recreatedEpisode.Path, ReadRequiredString(reboundCandidate!, "ItemPath"));

            var retryPersistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            retryPersistenceStub
                .Setup(x => x.SaveAsync(recreatedEpisode, CancellationToken.None))
                .Returns(Task.CompletedTask);

            var retryService = CreateService(candidateStore, retryPersistenceStub.Object, out _);

            await retryService.ProcessUpdatedItemAsync(CreateUpdate(recreatedEpisode, ItemUpdateType.MetadataDownload), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("皇后回宫", recreatedEpisode.Name);
            Assert.IsNull(candidateStore.Peek(recreatedEpisode.Id), "成功应用后应移除当前 itemId 对应的 candidate。");
            Assert.IsNull(PeekCandidateByPath(candidateStore, recreatedEpisode.Path), "成功应用后也应移除路径索引，避免遗留脏项。");
            retryPersistenceStub.Verify(x => x.SaveAsync(recreatedEpisode, CancellationToken.None), Times.Once);
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

            candidateStoreStub.Verify(x => x.Peek(It.IsAny<Guid>()), Times.Never);
            candidateStoreStub.Verify(x => x.Remove(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
            persistenceStub.Verify(x => x.SaveAsync(It.IsAny<Episode>(), It.IsAny<CancellationToken>()), Times.Never);
            AssertSkipLog(loggerStub, "UpdateReasonRejected", episode.Id, episode.Path!, episode.Name!, string.Empty, updateReason);
        }

        [TestMethod]
        public async Task ProcessUpdatedItemAsync_FeatureDisabled_DoesNotRemoveCandidateAndAllowsFutureRetry()
        {
            SetFeatureEnabled(false);

            var episodeId = Guid.NewGuid();
            var candidateStore = new InMemoryEpisodeTitleBackfillCandidateStore();
            candidateStore.Save(CreateCandidate(episodeId, "第 1 集", "皇后回宫"));
            var disabledEpisode = new Episode
            {
                Id = episodeId,
                Name = "第 1 集",
                Path = "/library/tv/series-a/Season 01/episode-01.mkv",
            };
            var retryEpisode = new Episode
            {
                Id = episodeId,
                Name = "第 1 集",
                Path = "/library/tv/series-a/Season 01/episode-01.mkv",
            };

            var persistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            persistenceStub
                .Setup(x => x.SaveAsync(retryEpisode, CancellationToken.None))
                .Returns(Task.CompletedTask);

            var service = CreateService(candidateStore, persistenceStub.Object, out var loggerStub);

            await service.ProcessUpdatedItemAsync(CreateUpdate(disabledEpisode, ItemUpdateType.MetadataImport), CancellationToken.None).ConfigureAwait(false);

            SetFeatureEnabled(true);
            await service.ProcessUpdatedItemAsync(CreateUpdate(retryEpisode, ItemUpdateType.MetadataDownload), CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("第 1 集", disabledEpisode.Name);
            Assert.AreEqual("皇后回宫", retryEpisode.Name);
            persistenceStub.Verify(x => x.SaveAsync(retryEpisode, CancellationToken.None), Times.Once);
            AssertSkipLog(loggerStub, "FeatureDisabled", episodeId, disabledEpisode.Path!, disabledEpisode.Name!, string.Empty, ItemUpdateType.MetadataImport);
        }

        [TestMethod]
        public async Task ProcessUpdatedItemAsync_NoCandidateReturned_DoesNotSave()
        {
            SetFeatureEnabled(true);

            var episode = CreateEpisode();
            var candidateStoreStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            candidateStoreStub
                .Setup(x => x.Peek(episode.Id))
                .Returns((EpisodeTitleBackfillCandidate?)null);

            var persistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            var service = CreateService(candidateStoreStub.Object, persistenceStub.Object, out var loggerStub);

            await service.ProcessUpdatedItemAsync(CreateUpdate(episode, ItemUpdateType.MetadataImport), CancellationToken.None).ConfigureAwait(false);

            candidateStoreStub.Verify(x => x.Peek(episode.Id), Times.Once);
            candidateStoreStub.Verify(x => x.Remove(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
            persistenceStub.Verify(x => x.SaveAsync(It.IsAny<Episode>(), It.IsAny<CancellationToken>()), Times.Never);
            AssertSkipLog(loggerStub, "NoCandidate", episode.Id, episode.Path!, episode.Name!, string.Empty, ItemUpdateType.MetadataImport);
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
                .Setup(x => x.Peek(episode.Id))
                .Returns(CreateCandidate(episode.Id));

            var persistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            var service = CreateService(candidateStoreStub.Object, persistenceStub.Object, out var loggerStub);

            await service.ProcessUpdatedItemAsync(CreateUpdate(episode, ItemUpdateType.MetadataImport), CancellationToken.None).ConfigureAwait(false);

            candidateStoreStub.Verify(x => x.Peek(episode.Id), Times.Once);
            candidateStoreStub.Verify(x => x.Remove(episode.Id, episode.Path), Times.Once);
            persistenceStub.Verify(x => x.SaveAsync(It.IsAny<Episode>(), It.IsAny<CancellationToken>()), Times.Never);
            AssertSkipLog(loggerStub, "Locked", episode.Id, episode.Path!, "第 1 集", "皇后回宫", ItemUpdateType.MetadataImport);
        }

        [TestMethod]
        public async Task ProcessUpdatedItemAsync_CurrentTitleSnapshotMismatch_DoesNotSave()
        {
            SetFeatureEnabled(true);

            var episode = CreateEpisode("  第 1 集  ");
            var candidateStoreStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            candidateStoreStub
                .Setup(x => x.Peek(episode.Id))
                .Returns(CreateCandidate(episode.Id, "  第 2 集  ", "皇后回宫"));

            var persistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            var service = CreateService(candidateStoreStub.Object, persistenceStub.Object, out var loggerStub);

            await service.ProcessUpdatedItemAsync(CreateUpdate(episode, ItemUpdateType.MetadataImport), CancellationToken.None).ConfigureAwait(false);

            candidateStoreStub.Verify(x => x.Remove(episode.Id, episode.Path), Times.Once);
            persistenceStub.Verify(x => x.SaveAsync(It.IsAny<Episode>(), It.IsAny<CancellationToken>()), Times.Never);
            AssertSkipLog(loggerStub, "TitleSnapshotMismatch", episode.Id, episode.Path!, "第 1 集", "皇后回宫", ItemUpdateType.MetadataImport, detail: "第 2 集");
        }

        [TestMethod]
        public async Task ProcessUpdatedItemAsync_CurrentTitleIsNotDefaultJellyfinEpisodeTitle_DoesNotSave()
        {
            SetFeatureEnabled(true);

            var episode = CreateEpisode("  重逢  ");
            var candidateStoreStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            candidateStoreStub
                .Setup(x => x.Peek(episode.Id))
                .Returns(CreateCandidate(episode.Id, " 重逢 ", "皇后回宫"));

            var persistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            var service = CreateService(candidateStoreStub.Object, persistenceStub.Object, out var loggerStub);

            await service.ProcessUpdatedItemAsync(CreateUpdate(episode, ItemUpdateType.MetadataImport), CancellationToken.None).ConfigureAwait(false);

            candidateStoreStub.Verify(x => x.Remove(episode.Id, episode.Path), Times.Once);
            persistenceStub.Verify(x => x.SaveAsync(It.IsAny<Episode>(), It.IsAny<CancellationToken>()), Times.Never);
            AssertSkipLog(loggerStub, "CurrentTitleNotDefault", episode.Id, episode.Path!, "重逢", "皇后回宫", ItemUpdateType.MetadataImport);
        }

        [TestMethod]
        public async Task ProcessUpdatedItemAsync_CurrentTitleAlreadyMatchesCandidateAfterTrim_DoesNotSave()
        {
            SetFeatureEnabled(true);

            var episode = CreateEpisode("  第 1 集  ");
            var candidateStoreStub = new Mock<IEpisodeTitleBackfillCandidateStore>();
            candidateStoreStub
                .Setup(x => x.Peek(episode.Id))
                .Returns(CreateCandidate(episode.Id, "第 1 集", "  第 1 集  "));

            var persistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            var service = CreateService(candidateStoreStub.Object, persistenceStub.Object, out var loggerStub);

            await service.ProcessUpdatedItemAsync(CreateUpdate(episode, ItemUpdateType.MetadataImport), CancellationToken.None).ConfigureAwait(false);

            candidateStoreStub.Verify(x => x.Remove(episode.Id, episode.Path), Times.Once);
            persistenceStub.Verify(x => x.SaveAsync(It.IsAny<Episode>(), It.IsAny<CancellationToken>()), Times.Never);
            AssertSkipLog(loggerStub, "CurrentEqualsCandidate", episode.Id, episode.Path!, "第 1 集", "第 1 集", ItemUpdateType.MetadataImport);
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
            AssertSkipLog(loggerStub, "NoCandidate", episodeId, firstEpisode.Path!, "第 1 集", string.Empty, ItemUpdateType.MetadataImport);
        }

        [TestMethod]
        public async Task TryApplyAsync_WhenDeferredRetryRacesItemUpdated_OnlyOnePersistenceCallWins()
        {
            SetFeatureEnabled(true);

            var episodeId = Guid.NewGuid();
            var itemPath = "/library/tv/series-a/Season 01/episode-01.mkv";
            var candidateStore = new InMemoryEpisodeTitleBackfillCandidateStore();
            candidateStore.Save(CreateCandidate(episodeId, "第 1 集", "皇后回宫", itemPath));

            var firstEpisode = new Episode
            {
                Id = episodeId,
                Name = "第 1 集",
                Path = itemPath,
            };
            var secondEpisode = new Episode
            {
                Id = episodeId,
                Name = "第 1 集",
                Path = itemPath,
            };

            var firstSaveEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstSave = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var persistenceCallCount = 0;
            var persistenceStub = new Mock<IEpisodeTitleBackfillPersistence>();
            persistenceStub
                .Setup(x => x.SaveAsync(It.IsAny<Episode>(), It.IsAny<CancellationToken>()))
                .Returns<Episode, CancellationToken>(async (_, cancellationToken) =>
                {
                    var currentCall = Interlocked.Increment(ref persistenceCallCount);
                    if (currentCall == 1)
                    {
                        firstSaveEntered.TrySetResult(true);
                        await releaseFirstSave.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    Assert.Fail("同一个 candidate 被并发观察时，只允许一个触发路径真正调用 SaveAsync。");
                });

            var resolver = new EpisodeTitleBackfillPendingResolver(candidateStore);
            var service = CreateService(candidateStore, resolver, persistenceStub.Object, out _);

            var firstApplyTask = service.TryApplyAsync(
                CreateUpdate(firstEpisode, ItemUpdateType.MetadataDownload),
                IEpisodeTitleBackfillPostProcessService.ItemUpdatedTrigger,
                CancellationToken.None);

            await firstSaveEntered.Task.ConfigureAwait(false);

            await service.TryApplyAsync(
                CreateUpdate(secondEpisode, ItemUpdateType.MetadataDownload),
                IEpisodeTitleBackfillPostProcessService.DeferredRetryTrigger,
                CancellationToken.None).ConfigureAwait(false);

            releaseFirstSave.TrySetResult(true);
            await firstApplyTask.ConfigureAwait(false);

            Assert.AreEqual(1, persistenceCallCount, "并发 ItemUpdated/DeferredRetry 只能有一个路径真正持久化标题。");
            Assert.IsNull(candidateStore.Peek(episodeId), "成功应用后 candidate 应被移除，避免后续再次 claim。");
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

            candidateStoreStub.Verify(x => x.Remove(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
            candidateStoreStub.Verify(x => x.Peek(It.IsAny<Guid>()), Times.Never);
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

            candidateStoreStub.Verify(x => x.Remove(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
            candidateStoreStub.Verify(x => x.Peek(It.IsAny<Guid>()), Times.Never);
            persistenceStub.Verify(x => x.SaveAsync(It.IsAny<Episode>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        private static EpisodeTitleBackfillPostProcessService CreateService(
            IEpisodeTitleBackfillCandidateStore candidateStore,
            IEpisodeTitleBackfillPersistence persistence,
            out Mock<ILogger<EpisodeTitleBackfillPostProcessService>> loggerStub)
        {
            loggerStub = new Mock<ILogger<EpisodeTitleBackfillPostProcessService>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            return new EpisodeTitleBackfillPostProcessService(candidateStore, new StoreBackedPendingResolver(candidateStore), persistence, loggerStub.Object);
        }

        private static EpisodeTitleBackfillPostProcessService CreateService(
            IEpisodeTitleBackfillCandidateStore candidateStore,
            IEpisodeTitleBackfillPendingResolver resolver,
            IEpisodeTitleBackfillPersistence persistence,
            out Mock<ILogger<EpisodeTitleBackfillPostProcessService>> loggerStub)
        {
            loggerStub = new Mock<ILogger<EpisodeTitleBackfillPostProcessService>>();
            loggerStub.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

            return new EpisodeTitleBackfillPostProcessService(candidateStore, resolver, persistence, loggerStub.Object);
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

        private static EpisodeTitleBackfillCandidate CreateCandidate(
            Guid itemId,
            string originalTitleSnapshot = "第 1 集",
            string candidateTitle = "皇后回宫",
            string itemPath = "/library/tv/series-a/Season 01/episode-01.mkv")
        {
            var nowUtc = DateTimeOffset.UtcNow;
            return new EpisodeTitleBackfillCandidate
            {
                ItemId = itemId,
                ItemPath = itemPath,
                OriginalTitleSnapshot = originalTitleSnapshot,
                CandidateTitle = candidateTitle,
                QueuedAtUtc = nowUtc,
                NextAttemptAtUtc = nowUtc,
                AttemptCount = 0,
                ExpiresAtUtc = nowUtc.AddMinutes(2),
            };
        }

        private static EpisodeTitleBackfillCandidate CreatePathAwareCandidate(Guid itemId, string itemPath, string originalTitleSnapshot = "第 1 集", string candidateTitle = "皇后回宫")
        {
            return CreateCandidate(itemId, originalTitleSnapshot, candidateTitle, itemPath);
        }

        private static EpisodeTitleBackfillCandidate? PeekCandidateByPath(InMemoryEpisodeTitleBackfillCandidateStore candidateStore, string itemPath)
        {
            var peekByPathMethod = typeof(InMemoryEpisodeTitleBackfillCandidateStore).GetMethod("PeekByPath", new[] { typeof(string) });
            Assert.IsNotNull(peekByPathMethod, "Expected path-aware PeekByPath(string) API for recreated-item recovery.");
            return peekByPathMethod!.Invoke(candidateStore, new object[] { itemPath }) as EpisodeTitleBackfillCandidate;
        }

        private static void AssertAppliedLog(
            Mock<ILogger<EpisodeTitleBackfillPostProcessService>> loggerStub,
            Guid itemId,
            string itemPath,
            string currentTitle,
            string candidateTitle,
            ItemUpdateType updateReason,
            string trigger = IEpisodeTitleBackfillPostProcessService.ItemUpdatedTrigger)
        {
            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Information,
                expectException: false,
                stateContains: new Dictionary<string, object?>
                {
                    ["ItemId"] = itemId,
                    ["Trigger"] = trigger,
                    ["ItemPath"] = itemPath,
                    ["CurrentTitle"] = currentTitle,
                    ["CandidateTitle"] = candidateTitle,
                    ["UpdateReason"] = updateReason,
                },
                originalFormatContains: "[MetaShark] 已应用剧集标题回填",
                messageContains: ["[MetaShark] 已应用剧集标题回填", $"trigger={trigger}", $"itemId={itemId}"]);
        }

        private static void AssertSkipLog(
            Mock<ILogger<EpisodeTitleBackfillPostProcessService>> loggerStub,
            string reason,
            Guid itemId,
            string itemPath,
            string currentTitle,
            string candidateTitle,
            ItemUpdateType updateReason,
            string trigger = IEpisodeTitleBackfillPostProcessService.ItemUpdatedTrigger,
            string? detail = null)
        {
            var stateContains = new Dictionary<string, object?>
            {
                ["Reason"] = reason,
                ["Trigger"] = trigger,
                ["ItemId"] = itemId,
                ["ItemPath"] = itemPath,
                ["CurrentTitle"] = currentTitle,
                ["CandidateTitle"] = candidateTitle,
                ["UpdateReason"] = updateReason,
            };

            var messageContains = new List<string>
            {
                "[MetaShark] 跳过剧集标题回填",
                $"reason={reason}",
                $"trigger={trigger}",
            };

            if (!string.IsNullOrWhiteSpace(detail))
            {
                stateContains["Detail"] = detail;
                messageContains.Add($"detail={detail}");
            }

            LogAssert.AssertLoggedOnce(
                loggerStub,
                LogLevel.Information,
                expectException: false,
                stateContains: stateContains,
                originalFormatContains: "[MetaShark] 跳过剧集标题回填",
                messageContains: [.. messageContains]);
        }

        private static void SetRequiredProperty<T>(EpisodeTitleBackfillCandidate candidate, string propertyName, T value)
        {
            var property = typeof(EpisodeTitleBackfillCandidate).GetProperty(propertyName);
            Assert.IsNotNull(property, $"EpisodeTitleBackfillCandidate is missing required property '{propertyName}'.");
            property!.SetValue(candidate, value);
        }

        private static string ReadRequiredString(EpisodeTitleBackfillCandidate candidate, string propertyName)
        {
            var property = typeof(EpisodeTitleBackfillCandidate).GetProperty(propertyName);
            Assert.IsNotNull(property, $"EpisodeTitleBackfillCandidate is missing required property '{propertyName}'.");
            return property!.GetValue(candidate) as string ?? string.Empty;
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

    internal static class EpisodeTitleBackfillPostProcessServiceTestExtensions
    {
        public static Task ProcessUpdatedItemAsync(this EpisodeTitleBackfillPostProcessService service, ItemChangeEventArgs e, CancellationToken cancellationToken)
        {
            return service.TryApplyAsync(e, IEpisodeTitleBackfillPostProcessService.ItemUpdatedTrigger, cancellationToken);
        }
    }

    internal sealed class StoreBackedPendingResolver : IEpisodeTitleBackfillPendingResolver
    {
        private readonly IEpisodeTitleBackfillCandidateStore candidateStore;

        public StoreBackedPendingResolver(IEpisodeTitleBackfillCandidateStore candidateStore)
        {
            this.candidateStore = candidateStore;
        }

        public EpisodeTitleBackfillCandidate? TryClaimForUpdatedEpisode(Episode episode, string claimToken)
        {
            var candidate = this.candidateStore.Peek(episode.Id);
            if (candidate != null)
            {
                return candidate;
            }

            if (string.IsNullOrWhiteSpace(episode.Path))
            {
                return null;
            }

            candidate = this.candidateStore.PeekByPath(episode.Path);
            if (candidate == null)
            {
                return null;
            }

            if (candidate.ItemId != episode.Id || !string.Equals(candidate.ItemPath, episode.Path, StringComparison.Ordinal))
            {
                candidate.ItemId = episode.Id;
                candidate.ItemPath = episode.Path;
                this.candidateStore.UpdateDeferredRetry(candidate);
            }

            return candidate;
        }

        public Episode? ResolveCurrentEpisode(EpisodeTitleBackfillCandidate candidate)
        {
            return null;
        }

        public void MarkDeferredAttempt(EpisodeTitleBackfillCandidate candidate, DateTimeOffset nowUtc)
        {
            candidate.AttemptCount += 1;
            candidate.NextAttemptAtUtc = nowUtc.AddSeconds(10);
            this.candidateStore.UpdateDeferredRetry(candidate);
        }

        public void ReleaseClaim(EpisodeTitleBackfillCandidate candidate, string claimToken)
        {
        }

        public void Complete(EpisodeTitleBackfillCandidate candidate)
        {
            this.candidateStore.Remove(candidate.ItemId, candidate.ItemPath);
        }

        public void Expire(EpisodeTitleBackfillCandidate candidate)
        {
            this.candidateStore.Remove(candidate.ItemId, candidate.ItemPath);
        }
    }
}
