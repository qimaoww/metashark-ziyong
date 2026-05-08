using System.Reflection;
using Jellyfin.Plugin.MetaShark.Providers;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class TmdbProviderIdPreservationTest
    {
        [TestMethod]
        public void PreserveMovieTmdbId_WhenFinalMovieLacksTmdbAndNoCorrection_RestoresOriginalTmdb()
        {
            var item = new Movie();

            TmdbProviderIdPreservationHelper.PreserveMovieTmdbId("12345", item, hasVerifiedCorrection: false);

            Assert.AreEqual("12345", item.GetProviderId(MetadataProvider.Tmdb));
        }

        [TestMethod]
        public void PreserveSeriesTmdbId_WhenVerifiedCorrectionProvided_KeepsNewTmdb()
        {
            var item = new Series();
            item.SetProviderId(MetadataProvider.Tmdb, "222");

            TmdbProviderIdPreservationHelper.PreserveSeriesTmdbId("111", item, hasVerifiedCorrection: true);

            Assert.AreEqual("222", item.GetProviderId(MetadataProvider.Tmdb));
        }

        [TestMethod]
        public void PreserveMovieTmdbId_WhenOriginalTmdbMissing_DoesNotAddTmdb()
        {
            var item = new Movie();

            TmdbProviderIdPreservationHelper.PreserveMovieTmdbId(null, item, hasVerifiedCorrection: false);

            Assert.IsNull(item.GetProviderId(MetadataProvider.Tmdb));
        }

        [TestMethod]
        public void PublicApi_ExposesOnlyMovieAndSeriesPreservationEntrypoints()
        {
            var publicMethods = typeof(TmdbProviderIdPreservationHelper)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.DeclaringType == typeof(TmdbProviderIdPreservationHelper))
                .ToArray();

            CollectionAssert.AreEquivalent(
                new[] { "PreserveMovieTmdbId", "PreserveSeriesTmdbId" },
                publicMethods.Select(method => method.Name).ToArray());
            AssertPublicItemParameter(publicMethods.Single(method => method.Name == "PreserveMovieTmdbId"), typeof(Movie));
            AssertPublicItemParameter(publicMethods.Single(method => method.Name == "PreserveSeriesTmdbId"), typeof(Series));
        }

        private static void AssertPublicItemParameter(MethodInfo method, Type expectedItemType)
        {
            var parameters = method.GetParameters();

            Assert.AreEqual(3, parameters.Length);
            Assert.AreEqual(typeof(string), parameters[0].ParameterType);
            Assert.AreEqual(expectedItemType, parameters[1].ParameterType);
            Assert.AreEqual(typeof(bool), parameters[2].ParameterType);
        }
    }
}
