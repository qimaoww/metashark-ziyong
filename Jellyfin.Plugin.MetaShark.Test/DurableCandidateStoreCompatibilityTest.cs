using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Workers;
using Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill;
using Microsoft.Extensions.Logging;
using Moq;
using System;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    [TestCategory("Stable")]
    public class DurableCandidateStoreCompatibilityTest
    {
        [TestMethod]
        public void EpisodeTitleBackfillStore_MatchesInMemorySaveClaimRemoveAndPathRebinding()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"metashark-title-candidate-{Guid.NewGuid():N}");
            try
            {
                var inMemoryStore = new InMemoryEpisodeTitleBackfillCandidateStore();
                var durableStore = new FileEpisodeTitleBackfillCandidateStore(
                    Path.Combine(tempRoot, "title-candidates.json"),
                    CreateLoggerFactory<FileEpisodeTitleBackfillCandidateStore>());

                var originalItemId = Guid.NewGuid();
                var reboundItemId = Guid.NewGuid();
                var itemPath = "/library/tv/series-a/Season 01/episode-01.mkv";
                var reboundPath = "/library/tv/series-a/Season 01/episode-01-recreated.mkv";
                var nowUtc = DateTimeOffset.UtcNow;
                var candidate = CreateTitleCandidate(
                    originalItemId,
                    itemPath,
                    nowUtc.AddMinutes(-2),
                    nowUtc.AddMinutes(-1),
                    attemptCount: 0,
                    nowUtc.AddMinutes(10));
                candidate.ClaimToken = "preexisting-claim";

                inMemoryStore.Save(candidate);
                durableStore.Save(candidate);

                AssertTitleCandidate(inMemoryStore.Peek(originalItemId), durableStore.Peek(originalItemId));
                Assert.AreEqual(string.Empty, durableStore.Peek(originalItemId)!.ClaimToken);

                var inMemoryClaim = inMemoryStore.TryClaim(originalItemId, itemPath, reboundItemId, reboundPath, "claim-a");
                var durableClaim = durableStore.TryClaim(originalItemId, itemPath, reboundItemId, reboundPath, "claim-a");

                AssertTitleCandidate(inMemoryClaim, durableClaim);
                Assert.IsNull(durableStore.TryClaim(reboundItemId, reboundPath, reboundItemId, reboundPath, "claim-b"));
                Assert.IsNull(durableStore.Peek(originalItemId));
                AssertTitleCandidate(inMemoryStore.PeekByPath(reboundPath), durableStore.PeekByPath(reboundPath));
                Assert.AreEqual(0, durableStore.GetDueDeferredRetries(nowUtc, 10).Count);

                durableStore.ReleaseClaim(reboundItemId, reboundPath, "wrong-token");
                Assert.IsNull(durableStore.TryClaim(reboundItemId, reboundPath, reboundItemId, reboundPath, "claim-c"));

                inMemoryStore.ReleaseClaim(reboundItemId, reboundPath, "claim-a");
                durableStore.ReleaseClaim(reboundItemId, reboundPath, "claim-a");

                AssertTitleCandidate(
                    inMemoryStore.TryClaim(reboundItemId, reboundPath, reboundItemId, reboundPath, "claim-c"),
                    durableStore.TryClaim(reboundItemId, reboundPath, reboundItemId, reboundPath, "claim-c"));

                inMemoryStore.Remove(reboundItemId, reboundPath);
                durableStore.Remove(reboundItemId, reboundPath);

                Assert.IsNull(durableStore.Peek(reboundItemId));
                Assert.IsNull(durableStore.PeekByPath(reboundPath));
            }
            finally
            {
                DeleteTempRoot(tempRoot);
            }
        }

        [TestMethod]
        public void EpisodeOverviewCleanupStore_MatchesInMemorySaveClaimRemoveAndPathRebinding()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"metashark-overview-candidate-{Guid.NewGuid():N}");
            try
            {
                var inMemoryStore = new InMemoryEpisodeOverviewCleanupCandidateStore();
                var durableStore = new FileEpisodeOverviewCleanupCandidateStore(
                    Path.Combine(tempRoot, "overview-candidates.json"),
                    CreateLoggerFactory<FileEpisodeOverviewCleanupCandidateStore>());

                var originalItemId = Guid.NewGuid();
                var reboundItemId = Guid.NewGuid();
                var itemPath = "/library/tv/series-b/Season 01/episode-02.mkv";
                var reboundPath = "/library/tv/series-b/Season 01/episode-02-recreated.mkv";
                var nowUtc = DateTimeOffset.UtcNow;
                var candidate = CreateOverviewCandidate(
                    originalItemId,
                    itemPath,
                    nowUtc.AddMinutes(-2),
                    nowUtc.AddMinutes(-1),
                    attemptCount: 1,
                    nowUtc.AddMinutes(10));
                candidate.ClaimToken = "preexisting-claim";

                inMemoryStore.Save(candidate);
                durableStore.Save(candidate);

                AssertOverviewCandidate(inMemoryStore.Peek(originalItemId), durableStore.Peek(originalItemId));
                Assert.AreEqual(string.Empty, durableStore.Peek(originalItemId)!.ClaimToken);

                var inMemoryClaim = inMemoryStore.TryClaim(originalItemId, itemPath, reboundItemId, reboundPath, "claim-a");
                var durableClaim = durableStore.TryClaim(originalItemId, itemPath, reboundItemId, reboundPath, "claim-a");

                AssertOverviewCandidate(inMemoryClaim, durableClaim);
                Assert.IsNull(durableStore.TryClaim(reboundItemId, reboundPath, reboundItemId, reboundPath, "claim-b"));
                Assert.IsNull(durableStore.Peek(originalItemId));
                AssertOverviewCandidate(inMemoryStore.PeekByPath(reboundPath), durableStore.PeekByPath(reboundPath));
                Assert.AreEqual(0, durableStore.GetDueDeferredRetries(nowUtc, 10).Count);

                durableStore.ReleaseClaim(reboundItemId, reboundPath, "wrong-token");
                Assert.IsNull(durableStore.TryClaim(reboundItemId, reboundPath, reboundItemId, reboundPath, "claim-c"));

                inMemoryStore.ReleaseClaim(reboundItemId, reboundPath, "claim-a");
                durableStore.ReleaseClaim(reboundItemId, reboundPath, "claim-a");

                AssertOverviewCandidate(
                    inMemoryStore.TryClaim(reboundItemId, reboundPath, reboundItemId, reboundPath, "claim-c"),
                    durableStore.TryClaim(reboundItemId, reboundPath, reboundItemId, reboundPath, "claim-c"));

                inMemoryStore.Remove(reboundItemId, reboundPath);
                durableStore.Remove(reboundItemId, reboundPath);

                Assert.IsNull(durableStore.Peek(reboundItemId));
                Assert.IsNull(durableStore.PeekByPath(reboundPath));
            }
            finally
            {
                DeleteTempRoot(tempRoot);
            }
        }

        [TestMethod]
        public void EpisodeTitleBackfillStore_PersistsDeferredRetryAndClaimStateAcrossInstances()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"metashark-title-candidate-persist-{Guid.NewGuid():N}");
            try
            {
                var stateFilePath = Path.Combine(tempRoot, "title-candidates.json");
                var loggerFactory = CreateLoggerFactory<FileEpisodeTitleBackfillCandidateStore>();
                var itemId = Guid.NewGuid();
                var itemPath = "/library/tv/series-c/Season 01/episode-03.mkv";
                var nowUtc = DateTimeOffset.UtcNow;
                var candidate = CreateTitleCandidate(
                    itemId,
                    itemPath,
                    nowUtc.AddMinutes(-3),
                    nowUtc.AddMinutes(-2),
                    attemptCount: 2,
                    nowUtc.AddMinutes(10));

                var firstStore = new FileEpisodeTitleBackfillCandidateStore(stateFilePath, loggerFactory);
                firstStore.Save(candidate);
                _ = firstStore.TryClaim(itemId, itemPath, itemId, itemPath, "claim-a");

                var claimedStore = new FileEpisodeTitleBackfillCandidateStore(stateFilePath, loggerFactory);
                Assert.AreEqual(0, claimedStore.GetDueDeferredRetries(nowUtc, 10).Count);
                claimedStore.ReleaseClaim(itemId, itemPath, "claim-a");

                var releasedStore = new FileEpisodeTitleBackfillCandidateStore(stateFilePath, loggerFactory);
                var dueCandidate = releasedStore.GetDueDeferredRetries(nowUtc, 10).Single();

                AssertTitleCandidate(candidate, dueCandidate);
            }
            finally
            {
                DeleteTempRoot(tempRoot);
            }
        }

        [TestMethod]
        public void EpisodeTitleBackfillStore_MatchesInMemoryWhenClaimRebindsToExistingPath()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), $"metashark-title-candidate-path-collision-{Guid.NewGuid():N}");
            try
            {
                var inMemoryStore = new InMemoryEpisodeTitleBackfillCandidateStore();
                var durableStore = new FileEpisodeTitleBackfillCandidateStore(
                    Path.Combine(tempRoot, "title-candidates.json"),
                    CreateLoggerFactory<FileEpisodeTitleBackfillCandidateStore>());

                var nowUtc = DateTimeOffset.UtcNow;
                var firstItemId = Guid.NewGuid();
                var secondItemId = Guid.NewGuid();
                var firstPath = "/library/tv/series-d/Season 01/episode-04-old.mkv";
                var sharedPath = "/library/tv/series-d/Season 01/episode-04.mkv";
                var firstCandidate = CreateTitleCandidate(firstItemId, firstPath, nowUtc.AddMinutes(-4), nowUtc.AddMinutes(-3), 0, nowUtc.AddMinutes(10));
                var secondCandidate = CreateTitleCandidate(secondItemId, sharedPath, nowUtc.AddMinutes(-2), nowUtc.AddMinutes(-1), 0, nowUtc.AddMinutes(10));

                inMemoryStore.Save(firstCandidate);
                inMemoryStore.Save(secondCandidate);
                durableStore.Save(firstCandidate);
                durableStore.Save(secondCandidate);

                _ = inMemoryStore.TryClaim(firstItemId, firstPath, firstItemId, sharedPath, "claim-a");
                _ = durableStore.TryClaim(firstItemId, firstPath, firstItemId, sharedPath, "claim-a");

                AssertTitleCandidate(inMemoryStore.Peek(firstItemId), durableStore.Peek(firstItemId));
                AssertTitleCandidate(inMemoryStore.Peek(secondItemId), durableStore.Peek(secondItemId));
                AssertTitleCandidate(inMemoryStore.PeekByPath(sharedPath), durableStore.PeekByPath(sharedPath));
            }
            finally
            {
                DeleteTempRoot(tempRoot);
            }
        }

        private static ILoggerFactory CreateLoggerFactory<TLogger>()
        {
            var loggerStub = new Mock<ILogger<TLogger>>();
            var loggerFactoryStub = new Mock<ILoggerFactory>();
            loggerFactoryStub.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(loggerStub.Object);
            return loggerFactoryStub.Object;
        }

        private static EpisodeTitleBackfillCandidate CreateTitleCandidate(
            Guid itemId,
            string itemPath,
            DateTimeOffset queuedAtUtc,
            DateTimeOffset nextAttemptAtUtc,
            int attemptCount,
            DateTimeOffset expiresAtUtc)
        {
            return new EpisodeTitleBackfillCandidate
            {
                ItemId = itemId,
                ItemPath = itemPath,
                OriginalTitleSnapshot = "第 1 集",
                CandidateTitle = "Queen Returns",
                QueuedAtUtc = queuedAtUtc,
                NextAttemptAtUtc = nextAttemptAtUtc,
                AttemptCount = attemptCount,
                ExpiresAtUtc = expiresAtUtc,
            };
        }

        private static EpisodeOverviewCleanupCandidate CreateOverviewCandidate(
            Guid itemId,
            string itemPath,
            DateTimeOffset queuedAtUtc,
            DateTimeOffset nextAttemptAtUtc,
            int attemptCount,
            DateTimeOffset expiresAtUtc)
        {
            return new EpisodeOverviewCleanupCandidate
            {
                ItemId = itemId,
                ItemPath = itemPath,
                OriginalOverviewSnapshot = "第 2 集简介",
                QueuedAtUtc = queuedAtUtc,
                NextAttemptAtUtc = nextAttemptAtUtc,
                AttemptCount = attemptCount,
                ExpiresAtUtc = expiresAtUtc,
            };
        }

        private static void AssertTitleCandidate(EpisodeTitleBackfillCandidate? expected, EpisodeTitleBackfillCandidate? actual)
        {
            Assert.IsNotNull(expected);
            Assert.IsNotNull(actual);
            Assert.AreNotSame(expected, actual);
            Assert.AreEqual(expected!.ItemId, actual!.ItemId);
            Assert.AreEqual(expected.ItemPath, actual.ItemPath);
            Assert.AreEqual(expected.OriginalTitleSnapshot, actual.OriginalTitleSnapshot);
            Assert.AreEqual(expected.CandidateTitle, actual.CandidateTitle);
            Assert.AreEqual(expected.QueuedAtUtc, actual.QueuedAtUtc);
            Assert.AreEqual(expected.NextAttemptAtUtc, actual.NextAttemptAtUtc);
            Assert.AreEqual(expected.AttemptCount, actual.AttemptCount);
            Assert.AreEqual(expected.ClaimToken, actual.ClaimToken);
            Assert.AreEqual(expected.ExpiresAtUtc, actual.ExpiresAtUtc);
        }

        private static void AssertOverviewCandidate(EpisodeOverviewCleanupCandidate? expected, EpisodeOverviewCleanupCandidate? actual)
        {
            Assert.IsNotNull(expected);
            Assert.IsNotNull(actual);
            Assert.AreNotSame(expected, actual);
            Assert.AreEqual(expected!.ItemId, actual!.ItemId);
            Assert.AreEqual(expected.ItemPath, actual.ItemPath);
            Assert.AreEqual(expected.OriginalOverviewSnapshot, actual.OriginalOverviewSnapshot);
            Assert.AreEqual(expected.QueuedAtUtc, actual.QueuedAtUtc);
            Assert.AreEqual(expected.NextAttemptAtUtc, actual.NextAttemptAtUtc);
            Assert.AreEqual(expected.AttemptCount, actual.AttemptCount);
            Assert.AreEqual(expected.ClaimToken, actual.ClaimToken);
            Assert.AreEqual(expected.ExpiresAtUtc, actual.ExpiresAtUtc);
        }

        private static void DeleteTempRoot(string tempRoot)
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
