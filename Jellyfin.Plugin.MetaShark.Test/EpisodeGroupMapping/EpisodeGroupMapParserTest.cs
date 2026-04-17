using System.Linq;
using Jellyfin.Plugin.MetaShark.EpisodeGroupMapping;

namespace Jellyfin.Plugin.MetaShark.Test.EpisodeGroupMapping
{
    [TestClass]
    [TestCategory("Stable")]
    public class EpisodeGroupMapParserTest
    {
        private readonly EpisodeGroupMapParser parser = new EpisodeGroupMapParser();

        [TestMethod]
        public void ParseSnapshot_KeepsFirstValidRecordWhenSeriesIdRepeats()
        {
            var snapshot = this.parser.ParseSnapshot(
                """
                65942 = 696e21a5491fd52f47aab23c
                65942 = ignored-later-group
                65942 = ignored-third-group
                """);

            Assert.IsTrue(snapshot.TryGetGroupId("65942", out var groupId));
            Assert.AreEqual("696e21a5491fd52f47aab23c", groupId);
            CollectionAssert.AreEqual(new[] { "65942" }, snapshot.MappedSeriesIds.ToArray());
            Assert.AreEqual(0, snapshot.InvalidWarnings.Count);
            Assert.AreEqual(2, snapshot.DuplicateWarnings.Count);
            Assert.AreEqual("65942=696e21a5491fd52f47aab23c", snapshot.CanonicalText);

            Assert.IsTrue(this.parser.TryGetGroupId("65942 = 696e21a5491fd52f47aab23c", "65942", out var parserGroupId));
            Assert.AreEqual("696e21a5491fd52f47aab23c", parserGroupId);
        }

        [TestMethod]
        public void ParseSnapshot_InvalidLinesDoNotPolluteLookup()
        {
            var snapshot = this.parser.ParseSnapshot(
                """
                invalid-line
                65942=
                =696e21a5491fd52f47aab23c
                70000 = valid-group
                """);

            Assert.IsFalse(snapshot.TryGetGroupId("65942", out _));
            Assert.IsFalse(this.parser.TryGetGroupId("invalid-line", "invalid-line", out _));
            Assert.IsTrue(snapshot.TryGetGroupId("70000", out var groupId));
            Assert.AreEqual("valid-group", groupId);
            CollectionAssert.AreEqual(new[] { "70000" }, this.parser.GetMappedSeriesIds(snapshot.CanonicalText).ToArray());
            Assert.AreEqual(3, snapshot.InvalidWarnings.Count);
            Assert.AreEqual(0, snapshot.DuplicateWarnings.Count);
        }

        [TestMethod]
        public void ParseSnapshot_IgnoresCommentsAndWhitespaceOnlyLines()
        {
            var snapshot = this.parser.ParseSnapshot(
                """

                    # comment line
                65942 = 696e21a5491fd52f47aab23c

                  # another comment
                80000=group-2

                """);

            Assert.IsTrue(snapshot.TryGetGroupId("65942", out var groupId));
            Assert.AreEqual("696e21a5491fd52f47aab23c", groupId);
            CollectionAssert.AreEqual(new[] { "65942", "80000" }, snapshot.MappedSeriesIds.ToArray());
            Assert.AreEqual(0, snapshot.InvalidWarnings.Count);
            Assert.AreEqual(0, snapshot.DuplicateWarnings.Count);
        }

        [TestMethod]
        public void ParseSnapshot_ProducesStableCanonicalTextSortedByKey()
        {
            const string Mapping =
                """
                303 = group-c
                # ignored comment
                101=group-a
                202 = group-b
                202 = duplicate-ignored
                invalid-line
                """;

            var snapshot = this.parser.ParseSnapshot(Mapping);

            Assert.AreEqual("101=group-a\n202=group-b\n303=group-c", snapshot.CanonicalText);
            Assert.AreEqual(snapshot.CanonicalText, this.parser.GetCanonicalText(Mapping));
            CollectionAssert.AreEqual(new[] { "101", "202", "303" }, this.parser.GetMappedSeriesIds(Mapping).ToArray());
            Assert.AreEqual(1, snapshot.InvalidWarnings.Count);
            Assert.AreEqual(1, snapshot.DuplicateWarnings.Count);
        }
    }
}
