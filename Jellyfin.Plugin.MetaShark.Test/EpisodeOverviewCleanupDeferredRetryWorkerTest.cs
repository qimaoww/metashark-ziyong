using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Workers;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Moq;
using System.Reflection;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class EpisodeOverviewCleanupDeferredRetryWorkerTest
    {
        [TestMethod]
        public async Task ExecuteDueCycleAsync_WhenPathFallbackFindsRecreatedEpisode_RebindsAndClearsOverview()
        {
            var itemPath = "/library/tv/series-a/Season 01/episode-01.mkv";
            var originalItemId = Guid.NewGuid();
            var recreatedEpisode = new Episode
            {
                Id = Guid.NewGuid(),
                Name = "第 1 集",
                Path = itemPath,
                Overview = "错误旧简介",
            };

            var candidateStore = new InMemoryEpisodeOverviewCleanupCandidateStore();
            candidateStore.Save(CreatePathAwareCandidate(originalItemId, itemPath, string.Empty, DateTimeOffset.UtcNow.AddSeconds(-30), DateTimeOffset.UtcNow.AddSeconds(-1)));

            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub.Setup(x => x.FindByPath(itemPath, false)).Returns(recreatedEpisode);

            var persistence = new RecordingEpisodeOverviewCleanupPersistence();
            var postProcessService = new EpisodeOverviewCleanupPostProcessService(candidateStore, persistence, LoggerFactory.Create(builder => { }).CreateLogger<EpisodeOverviewCleanupPostProcessService>());
            var worker = new EpisodeOverviewCleanupDeferredRetryWorker(candidateStore, new EpisodeOverviewCleanupPendingResolver(candidateStore, libraryManagerStub.Object), postProcessService, LoggerFactory.Create(builder => { }).CreateLogger<EpisodeOverviewCleanupDeferredRetryWorker>());

            await ExecuteDueCycleAsync(worker, DateTimeOffset.UtcNow).ConfigureAwait(false);

            Assert.AreEqual(null, recreatedEpisode.Overview);
            Assert.AreEqual(1, persistence.SaveCallCount);
            Assert.IsNull(candidateStore.Peek(recreatedEpisode.Id));
        }

        [TestMethod]
        public async Task ExecuteDueCycleAsync_WhenCandidateExpired_RemovesPendingWork()
        {
            var itemPath = "/library/tv/series-a/Season 01/episode-01.mkv";
            var nowUtc = DateTimeOffset.UtcNow;
            var candidateStore = new InMemoryEpisodeOverviewCleanupCandidateStore();
            candidateStore.Save(new EpisodeOverviewCleanupCandidate
            {
                ItemId = Guid.NewGuid(),
                ItemPath = itemPath,
                OriginalOverviewSnapshot = "错误旧简介",
                QueuedAtUtc = nowUtc.AddMinutes(-5),
                NextAttemptAtUtc = nowUtc.AddMinutes(-1),
                ExpiresAtUtc = nowUtc.AddSeconds(-1),
            });

            var persistence = new RecordingEpisodeOverviewCleanupPersistence();
            var postProcessService = new EpisodeOverviewCleanupPostProcessService(candidateStore, persistence, LoggerFactory.Create(builder => { }).CreateLogger<EpisodeOverviewCleanupPostProcessService>());
            var worker = new EpisodeOverviewCleanupDeferredRetryWorker(candidateStore, new EpisodeOverviewCleanupPendingResolver(candidateStore, new Mock<ILibraryManager>().Object), postProcessService, LoggerFactory.Create(builder => { }).CreateLogger<EpisodeOverviewCleanupDeferredRetryWorker>());

            await ExecuteDueCycleAsync(worker, nowUtc).ConfigureAwait(false);

            Assert.AreEqual(0, persistence.SaveCallCount);
            Assert.IsNull(PeekCandidateByPath(candidateStore, itemPath));
        }

        private static EpisodeOverviewCleanupCandidate CreatePathAwareCandidate(Guid itemId, string itemPath, string originalOverviewSnapshot, DateTimeOffset queuedAtUtc, DateTimeOffset nextAttemptAtUtc)
        {
            return new EpisodeOverviewCleanupCandidate
            {
                ItemId = itemId,
                ItemPath = itemPath,
                OriginalOverviewSnapshot = originalOverviewSnapshot,
                QueuedAtUtc = queuedAtUtc,
                NextAttemptAtUtc = nextAttemptAtUtc,
                ExpiresAtUtc = queuedAtUtc.AddMinutes(2),
            };
        }

        private static async Task ExecuteDueCycleAsync(EpisodeOverviewCleanupDeferredRetryWorker worker, DateTimeOffset nowUtc)
        {
            var executeMethod = worker.GetType().GetMethod(
                "ExecuteDueCycleAsync",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(DateTimeOffset), typeof(CancellationToken) },
                null);
            Assert.IsNotNull(executeMethod);

            var task = executeMethod!.Invoke(worker, new object[] { nowUtc, CancellationToken.None }) as Task;
            Assert.IsNotNull(task);
            await task!.ConfigureAwait(false);
        }

        private static EpisodeOverviewCleanupCandidate? PeekCandidateByPath(InMemoryEpisodeOverviewCleanupCandidateStore candidateStore, string itemPath)
        {
            var peekByPathMethod = typeof(InMemoryEpisodeOverviewCleanupCandidateStore).GetMethod("PeekByPath", new[] { typeof(string) });
            Assert.IsNotNull(peekByPathMethod);
            return peekByPathMethod!.Invoke(candidateStore, new object[] { itemPath }) as EpisodeOverviewCleanupCandidate;
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
    }
}
