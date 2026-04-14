using System;
using Jellyfin.Plugin.MetaShark.Model;
using Jellyfin.Plugin.MetaShark.Workers;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class EpisodeTitleBackfillCandidateStoreTest
    {
        [TestMethod]
        public void SaveThenConsume_ReturnsCandidate()
        {
            var itemId = Guid.NewGuid();
            var nowUtc = DateTimeOffset.UtcNow;
            var candidate = CreateCandidate(itemId, nowUtc.AddMinutes(-1), nowUtc.AddMinutes(10));
            var store = new InMemoryEpisodeTitleBackfillCandidateStore();

            store.Save(candidate);

            var consumed = store.Consume(itemId, nowUtc);

            AssertCandidate(candidate, consumed);
        }

        [TestMethod]
        public void Consume_RemovesCandidate_SecondConsumeReturnsNull()
        {
            var itemId = Guid.NewGuid();
            var nowUtc = DateTimeOffset.UtcNow;
            var candidate = CreateCandidate(itemId, nowUtc.AddMinutes(-1), nowUtc.AddMinutes(10));
            var store = new InMemoryEpisodeTitleBackfillCandidateStore();

            store.Save(candidate);

            var firstConsume = store.Consume(itemId, nowUtc);
            var secondConsume = store.Consume(itemId, nowUtc);

            AssertCandidate(candidate, firstConsume);
            Assert.IsNull(secondConsume);
        }

        [TestMethod]
        public void Consume_ExpiredCandidate_ReturnsNullAndClearsCandidate()
        {
            var itemId = Guid.NewGuid();
            var nowUtc = DateTimeOffset.UtcNow;
            var candidate = CreateCandidate(itemId, nowUtc.AddMinutes(-10), nowUtc.AddMinutes(-1));
            var store = new InMemoryEpisodeTitleBackfillCandidateStore();

            store.Save(candidate);

            var firstConsume = store.Consume(itemId, nowUtc);
            var secondConsume = store.Consume(itemId, nowUtc);

            Assert.IsNull(firstConsume);
            Assert.IsNull(secondConsume);
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

            var consumed = store.Consume(itemId, nowUtc);

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

            store.Remove(Guid.Empty);
            var consumed = store.Consume(itemId, nowUtc);

            AssertCandidate(candidate, consumed);
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

        private static void AssertCandidate(EpisodeTitleBackfillCandidate expected, EpisodeTitleBackfillCandidate? actual)
        {
            Assert.IsNotNull(actual);
            Assert.AreNotSame(expected, actual);
            Assert.AreEqual(expected.ItemId, actual.ItemId);
            Assert.AreEqual(expected.OriginalTitleSnapshot, actual.OriginalTitleSnapshot);
            Assert.AreEqual(expected.CandidateTitle, actual.CandidateTitle);
            Assert.AreEqual(expected.CreatedAtUtc, actual.CreatedAtUtc);
            Assert.AreEqual(expected.ExpiresAtUtc, actual.ExpiresAtUtc);
        }
    }
}
