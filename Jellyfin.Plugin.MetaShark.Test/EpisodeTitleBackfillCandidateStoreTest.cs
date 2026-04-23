using System;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Workers;
using Jellyfin.Plugin.MetaShark.Workers.EpisodeTitleBackfill;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class EpisodeTitleBackfillCandidateStoreTest
    {
        [TestMethod]
        public void SaveThenPeek_ReturnsCandidate()
        {
            var itemId = Guid.NewGuid();
            var nowUtc = DateTimeOffset.UtcNow;
            var candidate = CreateCandidate(itemId, nowUtc.AddMinutes(-1), nowUtc.AddMinutes(10));
            var store = new InMemoryEpisodeTitleBackfillCandidateStore();

            store.Save(candidate);

            var peeked = PeekCandidate(store, itemId);

            AssertCandidate(candidate, peeked);
        }

        [TestMethod]
        public void Save_ClearsClaimToken_AndPeekReturnsClone()
        {
            var itemId = Guid.NewGuid();
            var nowUtc = DateTimeOffset.UtcNow;
            var candidate = CreatePathAwareCandidate(
                itemId,
                "/library/tv/series-a/Season 01/episode-01.mkv",
                nowUtc.AddMinutes(-1),
                nowUtc.AddMinutes(10),
                attemptCount: 0,
                nowUtc.AddMinutes(10),
                originalTitleSnapshot: "第 1 集",
                candidateTitle: "皇后回宫");
            SetRequiredProperty(candidate, "ClaimToken", "claim-before-save");
            var store = new InMemoryEpisodeTitleBackfillCandidateStore();

            store.Save(candidate);

            var peeked = PeekCandidate(store, itemId);

            Assert.AreEqual(string.Empty, ReadString(candidate, "ClaimToken"), "Save 应该清空传入 candidate 的 claim token，延续当前保存行为。\n");
            Assert.IsNotNull(peeked);
            Assert.AreEqual(string.Empty, ReadString(peeked!, "ClaimToken"), "Peek 返回的副本应保持未 claim 状态。");
            AssertCandidate(candidate, peeked);
        }

        [TestMethod]
        public void Remove_RemovesCandidate_PeekReturnsNull()
        {
            var itemId = Guid.NewGuid();
            var nowUtc = DateTimeOffset.UtcNow;
            var candidate = CreateCandidate(itemId, nowUtc.AddMinutes(-1), nowUtc.AddMinutes(10));
            var store = new InMemoryEpisodeTitleBackfillCandidateStore();

            store.Save(candidate);
            store.Remove(itemId, string.Empty);

            var peeked = PeekCandidate(store, itemId);

            Assert.IsNull(peeked);
        }

        [TestMethod]
        public void Peek_ExpiredCandidate_ReturnsNullAndClearsCandidate()
        {
            var itemId = Guid.NewGuid();
            var nowUtc = DateTimeOffset.UtcNow;
            var candidate = CreateCandidate(itemId, nowUtc.AddMinutes(-10), nowUtc.AddMinutes(-1));
            var store = new InMemoryEpisodeTitleBackfillCandidateStore();

            store.Save(candidate);

            var firstPeek = PeekCandidate(store, itemId);
            var secondPeek = PeekCandidate(store, itemId);

            Assert.IsNull(firstPeek);
            Assert.IsNull(secondPeek);
        }

        [TestMethod]
        public void Save_SameItemIdTwice_SecondCandidateOverwritesFirst()
        {
            var itemId = Guid.NewGuid();
            var nowUtc = DateTimeOffset.UtcNow;
            var firstCandidate = CreateCandidate(itemId, nowUtc.AddMinutes(-2), nowUtc.AddMinutes(10), "Original A", "Candidate A");
            var secondCandidate = CreateCandidate(itemId, nowUtc.AddMinutes(-1), nowUtc.AddMinutes(20), "Original B", "Candidate B");
            var store = new InMemoryEpisodeTitleBackfillCandidateStore();

            store.Save(firstCandidate);
            store.Save(secondCandidate);

            var consumed = PeekCandidate(store, itemId);

            AssertCandidate(secondCandidate, consumed);
        }

        [TestMethod]
        public void Remove_EmptyGuid_IsNoOp()
        {
            var itemId = Guid.NewGuid();
            var nowUtc = DateTimeOffset.UtcNow;
            var candidate = CreateCandidate(itemId, nowUtc.AddMinutes(-1), nowUtc.AddMinutes(10));
            var store = new InMemoryEpisodeTitleBackfillCandidateStore();

            store.Save(candidate);

            store.Remove(Guid.Empty, string.Empty);
            var consumed = PeekCandidate(store, itemId);

            AssertCandidate(candidate, consumed);
        }

        [TestMethod]
        public void Peek_ReturnsCandidateWithoutRemoving()
        {
            var itemId = Guid.NewGuid();
            var nowUtc = DateTimeOffset.UtcNow;
            var candidate = CreateCandidate(itemId, nowUtc.AddMinutes(-1), nowUtc.AddMinutes(10));
            var store = new InMemoryEpisodeTitleBackfillCandidateStore();

            store.Save(candidate);

            var firstPeek = PeekCandidate(store, itemId);
            var secondPeek = PeekCandidate(store, itemId);

            AssertCandidate(candidate, firstPeek);
            AssertCandidate(candidate, secondPeek);
        }

        [TestMethod]
        public void PeekByPath_ReturnsCandidateWithoutRemoving()
        {
            var itemId = Guid.NewGuid();
            var itemPath = "/library/tv/series-a/Season 01/episode-01.mkv";
            var nowUtc = DateTimeOffset.UtcNow;
            var candidate = CreatePathAwareCandidate(
                itemId,
                itemPath,
                nowUtc.AddMinutes(-1),
                nowUtc.AddMinutes(10),
                attemptCount: 0,
                nowUtc.AddMinutes(10),
                originalTitleSnapshot: "第 1 集",
                candidateTitle: "皇后回宫");
            var store = new InMemoryEpisodeTitleBackfillCandidateStore();

            store.Save(candidate);

            var firstPeek = PeekCandidateByPath(store, itemPath);
            var secondPeek = PeekCandidateByPath(store, itemPath);

            AssertCandidate(candidate, firstPeek);
            AssertCandidate(candidate, secondPeek);
        }

        [TestMethod]
        public void Remove_DeletesCandidateAfterSuccessfulPeek()
        {
            var itemId = Guid.NewGuid();
            var nowUtc = DateTimeOffset.UtcNow;
            var candidate = CreateCandidate(itemId, nowUtc.AddMinutes(-1), nowUtc.AddMinutes(10));
            var store = new InMemoryEpisodeTitleBackfillCandidateStore();

            store.Save(candidate);

            var peeked = PeekCandidate(store, itemId);
            store.Remove(itemId, string.Empty);
            var secondPeek = PeekCandidate(store, itemId);

            AssertCandidate(candidate, peeked);
            Assert.IsNull(secondPeek);
        }

        [TestMethod]
        public void Peek_ReturnsNullForExpiredCandidate()
        {
            var itemId = Guid.NewGuid();
            var nowUtc = DateTimeOffset.UtcNow;
            var candidate = CreateCandidate(itemId, nowUtc.AddMinutes(-10), nowUtc.AddMinutes(-1));
            var store = new InMemoryEpisodeTitleBackfillCandidateStore();

            store.Save(candidate);

            var peeked = PeekCandidate(store, itemId);

            Assert.IsNull(peeked);
        }

        [TestMethod]
        public void PeekByPath_ExpiredCandidate_ReturnsNullAndClearsCandidate()
        {
            var itemId = Guid.NewGuid();
            var itemPath = "/library/tv/series-a/Season 01/episode-01.mkv";
            var nowUtc = DateTimeOffset.UtcNow;
            var candidate = CreatePathAwareCandidate(
                itemId,
                itemPath,
                nowUtc.AddMinutes(-10),
                nowUtc.AddMinutes(-1),
                attemptCount: 1,
                nowUtc.AddMinutes(-1),
                originalTitleSnapshot: "第 1 集",
                candidateTitle: "皇后回宫");
            var store = new InMemoryEpisodeTitleBackfillCandidateStore();

            store.Save(candidate);

            var firstPeek = PeekCandidateByPath(store, itemPath);
            var secondPeek = PeekCandidateByPath(store, itemPath);

            Assert.IsNull(firstPeek);
            Assert.IsNull(secondPeek);
            Assert.IsNull(PeekCandidate(store, itemId), "过期 candidate 应从 itemId 索引中清理。");
        }

        [TestMethod]
        public void PeekByPath_WhenItemIdChanges_StillReturnsCandidate()
        {
            var originalItemId = Guid.NewGuid();
            var recreatedItemId = Guid.NewGuid();
            var itemPath = "/library/tv/series-a/Season 01/episode-01.mkv";
            var nowUtc = DateTimeOffset.UtcNow;
            var originalCandidate = CreatePathAwareCandidate(
                originalItemId,
                itemPath,
                nowUtc.AddSeconds(-30),
                nowUtc.AddSeconds(-20),
                attemptCount: 0,
                nowUtc.AddMinutes(2),
                originalTitleSnapshot: "第 1 集",
                candidateTitle: "皇后回宫");
            var reboundCandidate = CreatePathAwareCandidate(
                recreatedItemId,
                itemPath,
                nowUtc.AddSeconds(-10),
                nowUtc.AddSeconds(10),
                attemptCount: 1,
                nowUtc.AddMinutes(2),
                originalTitleSnapshot: "第 1 集",
                candidateTitle: "皇后回宫");
            var store = new InMemoryEpisodeTitleBackfillCandidateStore();

            store.Save(originalCandidate);
            store.Save(reboundCandidate);

            var peekedByOriginalItemId = PeekCandidate(store, originalItemId);
            var peekedByRecreatedItemId = PeekCandidate(store, recreatedItemId);
            var peekedByPath = PeekCandidateByPath(store, itemPath);

            Assert.IsNull(peekedByOriginalItemId, "同一路径重新入队后，旧 itemId 不应继续持有 candidate。");
            AssertCandidate(reboundCandidate, peekedByRecreatedItemId);
            AssertCandidate(reboundCandidate, peekedByPath);
        }

        [TestMethod]
        public void GetDueDeferredRetries_WhenCandidateExpired_RemovesAndReturnsEmpty()
        {
            var itemId = Guid.NewGuid();
            var itemPath = "/library/tv/series-a/Season 01/episode-01.mkv";
            var nowUtc = DateTimeOffset.UtcNow;
            var candidate = CreatePathAwareCandidate(
                itemId,
                itemPath,
                nowUtc.AddMinutes(-5),
                nowUtc.AddMinutes(-1),
                attemptCount: 2,
                nowUtc.AddSeconds(-1),
                originalTitleSnapshot: "第 1 集",
                candidateTitle: "皇后回宫");
            var store = new InMemoryEpisodeTitleBackfillCandidateStore();

            store.Save(candidate);

            var dueCandidates = GetDueDeferredRetries(store, nowUtc, maxCount: 20);

            Assert.AreEqual(0, dueCandidates.Count, "过期的 candidate 不应再作为 deferred retry 返回。");
            Assert.IsNull(PeekCandidate(store, itemId), "过期 candidate 应从 itemId 索引中清理。");
            Assert.IsNull(PeekCandidateByPath(store, itemPath), "过期 candidate 应从路径索引中清理。");
        }

        [TestMethod]
        public void TryClaim_WhenCandidateAlreadyClaimed_SecondClaimReturnsNullUntilReleased()
        {
            var itemId = Guid.NewGuid();
            var itemPath = "/library/tv/series-a/Season 01/episode-01.mkv";
            var nowUtc = DateTimeOffset.UtcNow;
            var candidate = CreatePathAwareCandidate(
                itemId,
                itemPath,
                nowUtc.AddSeconds(-30),
                nowUtc.AddSeconds(-10),
                attemptCount: 0,
                nowUtc.AddMinutes(2),
                originalTitleSnapshot: "第 1 集",
                candidateTitle: "皇后回宫");
            var store = new InMemoryEpisodeTitleBackfillCandidateStore();

            store.Save(candidate);

            var firstClaim = TryClaim(store, itemId, itemPath, itemId, itemPath, "claim-a");
            var secondClaim = TryClaim(store, itemId, itemPath, itemId, itemPath, "claim-b");

            AssertCandidate(candidate, firstClaim);
            Assert.IsNull(secondClaim, "同一 candidate 被 claim 后，第二个触发路径不应再拿到它。");

            ReleaseClaim(store, itemId, itemPath, "claim-a");
            var thirdClaim = TryClaim(store, itemId, itemPath, itemId, itemPath, "claim-c");

            AssertCandidate(candidate, thirdClaim);
        }

        private static EpisodeTitleBackfillCandidate CreateCandidate(
            Guid itemId,
            DateTimeOffset createdAtUtc,
            DateTimeOffset expiresAtUtc,
            string originalTitleSnapshot = "Original Title",
            string candidateTitle = "Candidate Title")
        {
            return new EpisodeTitleBackfillCandidate
            {
                ItemId = itemId,
                OriginalTitleSnapshot = originalTitleSnapshot,
                CandidateTitle = candidateTitle,
                CreatedAtUtc = createdAtUtc,
                ExpiresAtUtc = expiresAtUtc,
            };
        }

        private static EpisodeTitleBackfillCandidate CreatePathAwareCandidate(
            Guid itemId,
            string itemPath,
            DateTimeOffset queuedAtUtc,
            DateTimeOffset nextAttemptAtUtc,
            int attemptCount,
            DateTimeOffset expiresAtUtc,
            string originalTitleSnapshot,
            string candidateTitle)
        {
            var candidate = CreateCandidate(itemId, queuedAtUtc, expiresAtUtc, originalTitleSnapshot, candidateTitle);
            SetRequiredProperty(candidate, "ItemPath", itemPath);
            SetRequiredProperty(candidate, "QueuedAtUtc", queuedAtUtc);
            SetRequiredProperty(candidate, "NextAttemptAtUtc", nextAttemptAtUtc);
            SetRequiredProperty(candidate, "AttemptCount", attemptCount);
            return candidate;
        }

        private static void AssertCandidate(EpisodeTitleBackfillCandidate expected, EpisodeTitleBackfillCandidate? actual)
        {
            Assert.IsNotNull(actual);
            Assert.AreNotSame(expected, actual);
            Assert.AreEqual(expected.ItemId, actual.ItemId);
            Assert.AreEqual(expected.OriginalTitleSnapshot, actual.OriginalTitleSnapshot);
            Assert.AreEqual(expected.CandidateTitle, actual.CandidateTitle);
            Assert.AreEqual(ReadDateTimeOffset(expected, "QueuedAtUtc"), ReadDateTimeOffset(actual, "QueuedAtUtc"));
            Assert.AreEqual(ReadDateTimeOffset(expected, "NextAttemptAtUtc"), ReadDateTimeOffset(actual, "NextAttemptAtUtc"));
            Assert.AreEqual(ReadInt32(expected, "AttemptCount"), ReadInt32(actual, "AttemptCount"));
            Assert.AreEqual(ReadString(expected, "ItemPath"), ReadString(actual, "ItemPath"));
            Assert.AreEqual(expected.ExpiresAtUtc, actual.ExpiresAtUtc);
        }

        private static EpisodeTitleBackfillCandidate? PeekCandidate(InMemoryEpisodeTitleBackfillCandidateStore store, Guid itemId)
        {
            var peekMethod = typeof(InMemoryEpisodeTitleBackfillCandidateStore).GetMethod("Peek", new[] { typeof(Guid) });
            Assert.IsNotNull(peekMethod, "Expected non-destructive Peek(Guid) API, but the store still only supports destructive Consume() semantics.");

            return peekMethod!.Invoke(store, new object[] { itemId }) as EpisodeTitleBackfillCandidate;
        }

        private static EpisodeTitleBackfillCandidate? PeekCandidateByPath(InMemoryEpisodeTitleBackfillCandidateStore store, string itemPath)
        {
            var peekByPathMethod = typeof(InMemoryEpisodeTitleBackfillCandidateStore).GetMethod("PeekByPath", new[] { typeof(string) });
            Assert.IsNotNull(peekByPathMethod, "Expected path-aware PeekByPath(string) API for recreated items.");

            return peekByPathMethod!.Invoke(store, new object[] { itemPath }) as EpisodeTitleBackfillCandidate;
        }

        private static System.Collections.Generic.IReadOnlyList<EpisodeTitleBackfillCandidate> GetDueDeferredRetries(
            InMemoryEpisodeTitleBackfillCandidateStore store,
            DateTimeOffset nowUtc,
            int maxCount)
        {
            var getDueMethod = typeof(InMemoryEpisodeTitleBackfillCandidateStore).GetMethod("GetDueDeferredRetries", new[] { typeof(DateTimeOffset), typeof(int) });
            Assert.IsNotNull(getDueMethod, "Expected deferred retry API GetDueDeferredRetries(DateTimeOffset, int).");

            var result = getDueMethod!.Invoke(store, new object[] { nowUtc, maxCount }) as System.Collections.Generic.IReadOnlyList<EpisodeTitleBackfillCandidate>;
            Assert.IsNotNull(result, "GetDueDeferredRetries should return a readable candidate list.");
            return result!;
        }

        private static EpisodeTitleBackfillCandidate? TryClaim(
            InMemoryEpisodeTitleBackfillCandidateStore store,
            Guid itemId,
            string itemPath,
            Guid currentItemId,
            string currentItemPath,
            string claimToken)
        {
            var tryClaimMethod = typeof(InMemoryEpisodeTitleBackfillCandidateStore).GetMethod("TryClaim", new[] { typeof(Guid), typeof(string), typeof(Guid), typeof(string), typeof(string) });
            Assert.IsNotNull(tryClaimMethod, "Expected TryClaim(Guid, string, Guid, string, string) API for atomic candidate ownership.");

            return tryClaimMethod!.Invoke(store, new object[] { itemId, itemPath, currentItemId, currentItemPath, claimToken }) as EpisodeTitleBackfillCandidate;
        }

        private static void ReleaseClaim(InMemoryEpisodeTitleBackfillCandidateStore store, Guid itemId, string itemPath, string claimToken)
        {
            var releaseClaimMethod = typeof(InMemoryEpisodeTitleBackfillCandidateStore).GetMethod("ReleaseClaim", new[] { typeof(Guid), typeof(string), typeof(string) });
            Assert.IsNotNull(releaseClaimMethod, "Expected ReleaseClaim(Guid, string, string) API for claim rollback.");
            releaseClaimMethod!.Invoke(store, new object[] { itemId, itemPath, claimToken });
        }

        private static void SetRequiredProperty<T>(EpisodeTitleBackfillCandidate candidate, string propertyName, T value)
        {
            var property = typeof(EpisodeTitleBackfillCandidate).GetProperty(propertyName);
            Assert.IsNotNull(property, $"EpisodeTitleBackfillCandidate is missing required property '{propertyName}'.");
            property!.SetValue(candidate, value);
        }

        private static string ReadString(EpisodeTitleBackfillCandidate candidate, string propertyName)
        {
            var property = typeof(EpisodeTitleBackfillCandidate).GetProperty(propertyName);
            Assert.IsNotNull(property, $"EpisodeTitleBackfillCandidate is missing required property '{propertyName}'.");
            return property!.GetValue(candidate) as string ?? string.Empty;
        }

        private static DateTimeOffset ReadDateTimeOffset(EpisodeTitleBackfillCandidate candidate, string propertyName)
        {
            var property = typeof(EpisodeTitleBackfillCandidate).GetProperty(propertyName);
            Assert.IsNotNull(property, $"EpisodeTitleBackfillCandidate is missing required property '{propertyName}'.");
            return (DateTimeOffset)(property!.GetValue(candidate) ?? default(DateTimeOffset));
        }

        private static int ReadInt32(EpisodeTitleBackfillCandidate candidate, string propertyName)
        {
            var property = typeof(EpisodeTitleBackfillCandidate).GetProperty(propertyName);
            Assert.IsNotNull(property, $"EpisodeTitleBackfillCandidate is missing required property '{propertyName}'.");
            return (int)(property!.GetValue(candidate) ?? 0);
        }
    }
}
