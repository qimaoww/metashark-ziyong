using Jellyfin.Plugin.MetaShark.Workers;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class TmdbCorrectionRefreshIntentStoreTest
    {
        [TestMethod]
        public void TryConsume_ReturnsTrueOnlyOnce_ForSameItemId()
        {
            var store = new InMemoryTmdbCorrectionRefreshIntentStore();
            var itemId = Guid.NewGuid();

            store.Save(itemId, "/library/Shows/ReZero");

            Assert.IsTrue(store.HasPending(itemId, "/library/Shows/ReZero"));
            Assert.IsTrue(store.TryConsume(itemId, "/library/Shows/ReZero"));
            Assert.IsFalse(store.HasPending(itemId, "/library/Shows/ReZero"));
            Assert.IsFalse(store.TryConsume(itemId, "/library/Shows/ReZero"));
        }

        [TestMethod]
        public void TryConsume_CanFallbackToPath_WhenItemIdChangesAcrossContexts()
        {
            var store = new InMemoryTmdbCorrectionRefreshIntentStore();
            var originalItemId = Guid.NewGuid();
            var reboundItemId = Guid.NewGuid();
            const string ItemPath = "/library/Movies/ReZero/ReZero.mkv";

            store.Save(originalItemId, ItemPath);

            Assert.IsTrue(store.TryConsume(reboundItemId, ItemPath));
            Assert.IsFalse(store.TryConsume(originalItemId, ItemPath));
        }

        [TestMethod]
        public void HasPending_CanSeparateSearchMissingAndOverwriteIntents()
        {
            var store = new InMemoryTmdbCorrectionRefreshIntentStore();
            var itemId = Guid.NewGuid();
            const string ItemPath = "/library/Shows/ReZero";

            store.SaveOverwrite(itemId, ItemPath);

            Assert.IsTrue(store.HasPendingOverwrite(itemId, ItemPath));
            Assert.IsFalse(store.HasPendingSearchMissing(itemId, ItemPath));

            store.SaveSearchMissing(itemId, ItemPath);

            Assert.IsTrue(store.HasPendingOverwrite(itemId, ItemPath));
            Assert.IsTrue(store.HasPendingSearchMissing(itemId, ItemPath));
        }
    }
}
