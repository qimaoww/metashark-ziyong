using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class EpisodeOverviewCleanupPostProcessServiceTest
    {
        [TestMethod]
        public async Task TryApplyAsync_MetadataDownloadWithEmptyOriginalSnapshotAndResurrectedOverview_ClearsOverviewAndPersists()
        {
            var episode = CreateEpisode("错误旧简介");
            var candidateStore = new InMemoryEpisodeOverviewCleanupCandidateStore();
            candidateStore.Save(CreateCandidate(episode.Id, episode.Path!, string.Empty));
            var persistence = new RecordingEpisodeOverviewCleanupPersistence();
            var service = CreateService(candidateStore, persistence);

            await service.TryApplyAsync(CreateUpdate(episode, ItemUpdateType.MetadataDownload), IEpisodeOverviewCleanupPostProcessService.ItemUpdatedTrigger, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(null, episode.Overview);
            Assert.AreEqual(1, persistence.SaveCallCount);
            Assert.IsNull(candidateStore.Peek(episode.Id));
        }

        [TestMethod]
        public async Task TryApplyAsync_CurrentOverviewAlreadyEmpty_DoesNotPersistAndConsumesCandidate()
        {
            var episode = CreateEpisode(string.Empty);
            var candidateStore = new InMemoryEpisodeOverviewCleanupCandidateStore();
            candidateStore.Save(CreateCandidate(episode.Id, episode.Path!, string.Empty));
            var persistence = new RecordingEpisodeOverviewCleanupPersistence();
            var service = CreateService(candidateStore, persistence);

            await service.TryApplyAsync(CreateUpdate(episode, ItemUpdateType.MetadataDownload), IEpisodeOverviewCleanupPostProcessService.ItemUpdatedTrigger, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(string.Empty, episode.Overview);
            Assert.AreEqual(0, persistence.SaveCallCount);
            Assert.IsNull(candidateStore.Peek(episode.Id));
        }

        [TestMethod]
        public async Task TryApplyAsync_LockedEpisodeOrOverviewField_DoesNotPersistAndConsumesCandidate()
        {
            var episode = CreateEpisode("错误旧简介");
            episode.LockedFields = new[] { MetadataField.Overview };
            var candidateStore = new InMemoryEpisodeOverviewCleanupCandidateStore();
            candidateStore.Save(CreateCandidate(episode.Id, episode.Path!, "错误旧简介"));
            var persistence = new RecordingEpisodeOverviewCleanupPersistence();
            var service = CreateService(candidateStore, persistence);

            await service.TryApplyAsync(CreateUpdate(episode, ItemUpdateType.MetadataDownload), IEpisodeOverviewCleanupPostProcessService.ItemUpdatedTrigger, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("错误旧简介", episode.Overview);
            Assert.AreEqual(0, persistence.SaveCallCount);
            Assert.IsNull(candidateStore.Peek(episode.Id));
        }

        [TestMethod]
        public async Task TryApplyAsync_NonEmptyOriginalSnapshotMismatch_DoesNotPersistAndConsumesCandidate()
        {
            var episode = CreateEpisode("别的简介");
            var candidateStore = new InMemoryEpisodeOverviewCleanupCandidateStore();
            candidateStore.Save(CreateCandidate(episode.Id, episode.Path!, "错误旧简介"));
            var persistence = new RecordingEpisodeOverviewCleanupPersistence();
            var service = CreateService(candidateStore, persistence);

            await service.TryApplyAsync(CreateUpdate(episode, ItemUpdateType.MetadataDownload), IEpisodeOverviewCleanupPostProcessService.ItemUpdatedTrigger, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("别的简介", episode.Overview);
            Assert.AreEqual(0, persistence.SaveCallCount);
            Assert.IsNull(candidateStore.Peek(episode.Id));
        }

        [TestMethod]
        public async Task TryApplyAsync_CurrentOverviewSnapshotMismatch_DoesNotPersistAndConsumesCandidate()
        {
            var episode = CreateEpisode("别的简介");
            var candidateStore = new InMemoryEpisodeOverviewCleanupCandidateStore();
            candidateStore.Save(CreateCandidate(episode.Id, episode.Path!, "错误旧简介"));
            var persistence = new RecordingEpisodeOverviewCleanupPersistence();
            var service = CreateService(candidateStore, persistence);

            await service.TryApplyAsync(CreateUpdate(episode, ItemUpdateType.MetadataDownload), IEpisodeOverviewCleanupPostProcessService.ItemUpdatedTrigger, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("别的简介", episode.Overview);
            Assert.AreEqual(0, persistence.SaveCallCount);
            Assert.IsNull(candidateStore.Peek(episode.Id));
        }

        [TestMethod]
        public async Task TryApplyAsync_NonEmptyOriginalSnapshotSameAsCurrent_DoesNotPersistAndConsumesCandidate()
        {
            var episode = CreateEpisode("合法既有简介");
            var candidateStore = new InMemoryEpisodeOverviewCleanupCandidateStore();
            candidateStore.Save(CreateCandidate(episode.Id, episode.Path!, "合法既有简介"));
            var persistence = new RecordingEpisodeOverviewCleanupPersistence();
            var service = CreateService(candidateStore, persistence);

            await service.TryApplyAsync(CreateUpdate(episode, ItemUpdateType.MetadataDownload), IEpisodeOverviewCleanupPostProcessService.ItemUpdatedTrigger, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual("合法既有简介", episode.Overview);
            Assert.AreEqual(0, persistence.SaveCallCount);
            Assert.IsNull(candidateStore.Peek(episode.Id));
        }

        [TestMethod]
        public async Task TryApplyAsync_CleanupSuccess_DoesNotLogOriginalOverviewTextAtInformation()
        {
            using var loggerProvider = new TestLoggerProvider();
            using var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Information).AddProvider(loggerProvider));

            var episode = CreateEpisode("错误旧简介");
            var candidateStore = new InMemoryEpisodeOverviewCleanupCandidateStore();
            candidateStore.Save(CreateCandidate(episode.Id, episode.Path!, string.Empty));
            var persistence = new RecordingEpisodeOverviewCleanupPersistence();
            var service = new EpisodeOverviewCleanupPostProcessService(candidateStore, persistence, loggerFactory.CreateLogger<EpisodeOverviewCleanupPostProcessService>());

            await service.TryApplyAsync(CreateUpdate(episode, ItemUpdateType.MetadataDownload), IEpisodeOverviewCleanupPostProcessService.ItemUpdatedTrigger, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, persistence.SaveCallCount);
            Assert.IsFalse(
                loggerProvider.Messages.Any(message => message.LogLevel == LogLevel.Information
                    && message.Category.Contains(nameof(EpisodeOverviewCleanupPostProcessService), StringComparison.Ordinal)
                    && (message.Message.Contains("originalOverview=", StringComparison.Ordinal)
                        || message.Message.Contains("错误旧简介", StringComparison.Ordinal))),
                "cleanup 成功路径不应在 Information 日志中继续输出 original overview 文本或原文键名。 ");
        }

        [TestMethod]
        public async Task TryApplyAsync_WhenPersistenceThrows_RestoresOverviewAndKeepsCandidate()
        {
            var episode = CreateEpisode("错误旧简介");
            var candidateStore = new InMemoryEpisodeOverviewCleanupCandidateStore();
            candidateStore.Save(CreateCandidate(episode.Id, episode.Path!, string.Empty));
            var persistence = new Mock<IEpisodeOverviewCleanupPersistence>();
            persistence.Setup(x => x.SaveAsync(episode, CancellationToken.None)).ThrowsAsync(new InvalidOperationException("boom"));
            var service = CreateService(candidateStore, persistence.Object);

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.TryApplyAsync(CreateUpdate(episode, ItemUpdateType.MetadataDownload), IEpisodeOverviewCleanupPostProcessService.ItemUpdatedTrigger, CancellationToken.None)).ConfigureAwait(false);

            Assert.AreEqual("错误旧简介", episode.Overview);
            Assert.IsNotNull(candidateStore.Peek(episode.Id));
        }

        private static EpisodeOverviewCleanupPostProcessService CreateService(IEpisodeOverviewCleanupCandidateStore candidateStore, IEpisodeOverviewCleanupPersistence persistence)
        {
            return new EpisodeOverviewCleanupPostProcessService(candidateStore, persistence, LoggerFactory.Create(builder => { }).CreateLogger<EpisodeOverviewCleanupPostProcessService>());
        }

        private static ItemChangeEventArgs CreateUpdate(Episode episode, ItemUpdateType updateReason)
        {
            return new ItemChangeEventArgs
            {
                Item = episode,
                UpdateReason = updateReason,
            };
        }

        private static Episode CreateEpisode(string overview)
        {
            return new Episode
            {
                Id = Guid.NewGuid(),
                Name = "第 1 集",
                Path = "/library/tv/series-a/Season 01/episode-01.mkv",
                Overview = overview,
            };
        }

        private static EpisodeOverviewCleanupCandidate CreateCandidate(Guid itemId, string itemPath, string originalOverviewSnapshot)
        {
            return new EpisodeOverviewCleanupCandidate
            {
                ItemId = itemId,
                ItemPath = itemPath,
                OriginalOverviewSnapshot = originalOverviewSnapshot,
                QueuedAtUtc = DateTimeOffset.UtcNow,
                NextAttemptAtUtc = DateTimeOffset.UtcNow.AddSeconds(10),
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(2),
            };
        }

        private sealed class RecordingEpisodeOverviewCleanupPersistence : IEpisodeOverviewCleanupPersistence
        {
            public int SaveCallCount { get; private set; }

            public Task SaveAsync(Episode episode, CancellationToken cancellationToken)
            {
                this.SaveCallCount++;
                return Task.CompletedTask;
            }
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
