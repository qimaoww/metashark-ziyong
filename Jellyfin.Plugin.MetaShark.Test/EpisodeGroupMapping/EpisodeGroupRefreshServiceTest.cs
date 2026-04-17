using System.Linq;
using Jellyfin.Plugin.MetaShark.EpisodeGroupMapping;

namespace Jellyfin.Plugin.MetaShark.Test.EpisodeGroupMapping
{
    [TestClass]
    [TestCategory("Stable")]
    public class EpisodeGroupRefreshServiceTest
    {
        private readonly EpisodeGroupRefreshService service = new();

        [TestMethod]
        public void CreateRefreshResult_AddMapping_AffectsNewSeriesId()
        {
            var result = this.service.CreateRefreshResult(
                oldMapping: string.Empty,
                newMapping: "65942=696e21a5491fd52f47aab23c");

            CollectionAssert.AreEqual(new[] { "65942" }, result.AddedSeriesIds.ToArray());
            CollectionAssert.AreEqual(new[] { "65942" }, result.AffectedSeriesIds.ToArray());
            Assert.IsFalse(result.IsNoOp);
        }

        [TestMethod]
        public void CreateRefreshResult_DeleteMapping_AffectsOldSeriesId()
        {
            var result = this.service.CreateRefreshResult(
                oldMapping: "65942=696e21a5491fd52f47aab23c",
                newMapping: string.Empty);

            CollectionAssert.AreEqual(new[] { "65942" }, result.RemovedSeriesIds.ToArray());
            CollectionAssert.AreEqual(new[] { "65942" }, result.AffectedSeriesIds.ToArray());
            Assert.IsFalse(result.IsNoOp);
        }

        [TestMethod]
        public void CreateRefreshResult_GroupIdChanged_AffectsSharedSeriesId()
        {
            var result = this.service.CreateRefreshResult(
                oldMapping: "65942=old-group",
                newMapping: "65942=new-group");

            CollectionAssert.AreEqual(new[] { "65942" }, result.ChangedSeriesIds.ToArray());
            CollectionAssert.AreEqual(new[] { "65942" }, result.AffectedSeriesIds.ToArray());
            Assert.IsFalse(result.IsNoOp);
        }

        [TestMethod]
        public void CreateRefreshResult_WhitespaceCommentAndOrderOnlyChange_IsSemanticNoOp()
        {
            var result = this.service.CreateRefreshResult(
                oldMapping: "65942=group-a\n70000=group-b",
                newMapping:
                """
                # comment line
                70000 = group-b
                65942 = group-a
                """);

            Assert.AreEqual(0, result.AffectedSeriesIds.Count);
            Assert.IsTrue(result.IsNoOp);
        }

        [TestMethod]
        public void CreateRefreshResult_EmptyOldMapping_RepresentsMissingBodyFallbackAgainstCurrentConfig()
        {
            const string CurrentMapping =
                """
                invalid-line
                65942=group-a
                70000 = group-b
                """;

            var result = this.service.CreateRefreshResult(
                oldMapping: string.Empty,
                newMapping: CurrentMapping);

            CollectionAssert.AreEqual(new[] { "65942", "70000" }, result.AffectedSeriesIds.ToArray());
            CollectionAssert.AreEqual(new[] { "65942", "70000" }, result.AddedSeriesIds.ToArray());
            Assert.AreEqual(0, result.OldInvalidWarningCount);
            Assert.AreEqual(1, result.NewInvalidWarningCount);
            Assert.IsFalse(result.IsNoOp);
        }
    }
}
