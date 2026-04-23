using System;
using Jellyfin.Plugin.MetaShark.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Moq;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class MetaSharkOrdinaryItemLibraryCapabilityResolverTest
    {
        [TestMethod]
        public void Resolve_MovieMetadataEnabledWhileImageDisabled_ShouldSplitCapabilitiesByFetcherList()
        {
            var movie = new Movie { Id = Guid.NewGuid(), Name = "Movie A", Path = "/library/movies/movie-a.mkv" };
            var resolver = CreateResolver(
                movie,
                CreateLibraryOptions(
                    CreateTypeOptions(
                        nameof(Movie),
                        metadataFetchers: new[] { MetaSharkPlugin.PluginName },
                        imageFetchers: Array.Empty<string>())));

            var metadataDecision = resolver.Resolve(movie, MetaSharkLibraryCapability.Metadata);
            var imageDecision = resolver.Resolve(movie, MetaSharkLibraryCapability.Image);

            Assert.IsTrue(metadataDecision.Allowed);
            Assert.AreEqual(MetaSharkLibraryCapabilityGateReason.Allowed, metadataDecision.Reason);
            Assert.AreEqual(1, metadataDecision.ResolvedLibraries.Count);
            Assert.AreEqual(nameof(Movie), metadataDecision.ResolvedLibraries[0].ItemType);
            Assert.IsTrue(metadataDecision.ResolvedLibraries[0].MetadataAllowed);
            Assert.IsFalse(metadataDecision.ResolvedLibraries[0].ImageAllowed);

            Assert.IsFalse(imageDecision.Allowed);
            Assert.AreEqual(MetaSharkLibraryCapabilityGateReason.CapabilityDisabledForResolvedLibrary, imageDecision.Reason);
            Assert.AreEqual(1, imageDecision.ResolvedLibraries.Count);
            Assert.AreEqual(nameof(Movie), imageDecision.ResolvedLibraries[0].ItemType);
        }

        [TestMethod]
        public void Resolve_SeriesImageEnabledWhileMetadataDisabled_ShouldSplitCapabilitiesByFetcherList()
        {
            var series = new Series { Id = Guid.NewGuid(), Name = "Series A", Path = "/library/tv/series-a" };
            var resolver = CreateResolver(
                series,
                CreateLibraryOptions(
                    CreateTypeOptions(
                        nameof(Series),
                        metadataFetchers: Array.Empty<string>(),
                        imageFetchers: new[] { MetaSharkPlugin.PluginName })));

            var metadataDecision = resolver.Resolve(series, MetaSharkLibraryCapability.Metadata);
            var imageDecision = resolver.Resolve(series, MetaSharkLibraryCapability.Image);

            Assert.IsFalse(metadataDecision.Allowed);
            Assert.AreEqual(MetaSharkLibraryCapabilityGateReason.CapabilityDisabledForResolvedLibrary, metadataDecision.Reason);
            Assert.AreEqual(1, metadataDecision.ResolvedLibraries.Count);
            Assert.AreEqual(nameof(Series), metadataDecision.ResolvedLibraries[0].ItemType);
            Assert.IsFalse(metadataDecision.ResolvedLibraries[0].MetadataAllowed);
            Assert.IsTrue(metadataDecision.ResolvedLibraries[0].ImageAllowed);

            Assert.IsTrue(imageDecision.Allowed);
            Assert.AreEqual(MetaSharkLibraryCapabilityGateReason.Allowed, imageDecision.Reason);
            Assert.AreEqual(1, imageDecision.ResolvedLibraries.Count);
            Assert.AreEqual(nameof(Series), imageDecision.ResolvedLibraries[0].ItemType);
        }

        [TestMethod]
        public void Resolve_MovieWithMetadataAndImageDisabled_ShouldFailClosedForBothCapabilities()
        {
            var movie = new Movie { Id = Guid.NewGuid(), Name = "Movie A", Path = "/library/movies/movie-a.mkv" };
            var resolver = CreateResolver(
                movie,
                CreateLibraryOptions(
                    CreateTypeOptions(
                        nameof(Movie),
                        metadataFetchers: Array.Empty<string>(),
                        imageFetchers: Array.Empty<string>())));

            var metadataDecision = resolver.Resolve(movie, MetaSharkLibraryCapability.Metadata);
            var imageDecision = resolver.Resolve(movie, MetaSharkLibraryCapability.Image);

            Assert.IsFalse(metadataDecision.Allowed);
            Assert.AreEqual(MetaSharkLibraryCapabilityGateReason.CapabilityDisabledForResolvedLibrary, metadataDecision.Reason);
            Assert.AreEqual(1, metadataDecision.ResolvedLibraries.Count);
            Assert.AreEqual(nameof(Movie), metadataDecision.ResolvedLibraries[0].ItemType);
            Assert.IsFalse(metadataDecision.ResolvedLibraries[0].MetadataAllowed);
            Assert.IsFalse(metadataDecision.ResolvedLibraries[0].ImageAllowed);

            Assert.IsFalse(imageDecision.Allowed);
            Assert.AreEqual(MetaSharkLibraryCapabilityGateReason.CapabilityDisabledForResolvedLibrary, imageDecision.Reason);
            Assert.AreEqual(1, imageDecision.ResolvedLibraries.Count);
            Assert.AreEqual(nameof(Movie), imageDecision.ResolvedLibraries[0].ItemType);
            Assert.IsFalse(imageDecision.ResolvedLibraries[0].MetadataAllowed);
            Assert.IsFalse(imageDecision.ResolvedLibraries[0].ImageAllowed);
        }

        [TestMethod]
        public void Resolve_SeasonMetadata_ShouldUseSeasonTypeOptionsInsteadOfSeries()
        {
            var season = new Season { Id = Guid.NewGuid(), Name = "Season 1", Path = "/library/tv/series-a/Season 01" };
            var resolver = CreateResolver(
                season,
                CreateLibraryOptions(
                    CreateTypeOptions(nameof(Series), metadataFetchers: new[] { MetaSharkPlugin.PluginName }),
                    CreateTypeOptions(nameof(Season), metadataFetchers: Array.Empty<string>())));

            var decision = resolver.Resolve(season, MetaSharkLibraryCapability.Metadata);

            Assert.IsFalse(decision.Allowed);
            Assert.AreEqual(MetaSharkLibraryCapabilityGateReason.CapabilityDisabledForResolvedLibrary, decision.Reason);
            Assert.AreEqual(1, decision.ResolvedLibraries.Count);
            Assert.AreEqual(nameof(Season), decision.ResolvedLibraries[0].ItemType);
            Assert.IsFalse(decision.ResolvedLibraries[0].MetadataAllowed);
        }

        [TestMethod]
        public void Resolve_EpisodeImage_ShouldUseEpisodeTypeOptionsInsteadOfSeries()
        {
            var episode = new Episode { Id = Guid.NewGuid(), Name = "Episode 1", Path = "/library/tv/series-a/Season 01/episode-01.mkv" };
            var resolver = CreateResolver(
                episode,
                CreateLibraryOptions(
                    CreateTypeOptions(nameof(Series), imageFetchers: new[] { MetaSharkPlugin.PluginName }),
                    CreateTypeOptions(nameof(Episode), imageFetchers: Array.Empty<string>())));

            var decision = resolver.Resolve(episode, MetaSharkLibraryCapability.Image);

            Assert.IsFalse(decision.Allowed);
            Assert.AreEqual(MetaSharkLibraryCapabilityGateReason.CapabilityDisabledForResolvedLibrary, decision.Reason);
            Assert.AreEqual(1, decision.ResolvedLibraries.Count);
            Assert.AreEqual(nameof(Episode), decision.ResolvedLibraries[0].ItemType);
            Assert.IsFalse(decision.ResolvedLibraries[0].ImageAllowed);
        }

        [TestMethod]
        public void Resolve_MissingMatchingTypeOptions_ShouldReturnDenyDecisionInsteadOfThrowing()
        {
            var movie = new Movie { Id = Guid.NewGuid(), Name = "Movie A", Path = "/library/movies/movie-a.mkv" };
            var resolver = CreateResolver(
                movie,
                CreateLibraryOptions(
                    CreateTypeOptions(nameof(Series), metadataFetchers: new[] { MetaSharkPlugin.PluginName })));

            var decision = resolver.Resolve(movie, MetaSharkLibraryCapability.Metadata);

            Assert.IsFalse(decision.Allowed);
            Assert.AreEqual(MetaSharkLibraryCapabilityGateReason.NoResolvedLibraryEvidence, decision.Reason);
            Assert.AreEqual(MetaSharkLibraryCapability.Metadata, decision.Capability);
            Assert.AreEqual(0, decision.ResolvedLibraries.Count);
        }

        [TestMethod]
        public void Resolve_MissingLibraryOptions_ShouldReturnDenyDecisionInsteadOfThrowing()
        {
            var movie = new Movie { Id = Guid.NewGuid(), Name = "Movie A", Path = "/library/movies/movie-a.mkv" };
            var resolver = CreateResolver(movie, null);

            var decision = resolver.Resolve(movie, MetaSharkLibraryCapability.Image);

            Assert.IsFalse(decision.Allowed);
            Assert.AreEqual(MetaSharkLibraryCapabilityGateReason.NoResolvedLibraryEvidence, decision.Reason);
            Assert.AreEqual(MetaSharkLibraryCapability.Image, decision.Capability);
            Assert.AreEqual(0, decision.ResolvedLibraries.Count);
        }

        [TestMethod]
        public void Resolve_UnsupportedItemType_ShouldReturnSafeDenyDecision()
        {
            var person = new Person { Id = Guid.NewGuid(), Name = "Actor A", Path = "/library/people/actor-a" };
            var resolver = CreateResolver(person, CreateLibraryOptions());

            var decision = resolver.Resolve(person, MetaSharkLibraryCapability.Metadata);

            Assert.IsFalse(decision.Allowed);
            Assert.AreEqual(MetaSharkLibraryCapabilityGateReason.NoResolvedLibraryEvidence, decision.Reason);
            Assert.AreEqual(MetaSharkLibraryCapability.Metadata, decision.Capability);
            Assert.AreEqual(0, decision.ResolvedLibraries.Count);
        }

        private static MetaSharkOrdinaryItemLibraryCapabilityResolver CreateResolver(BaseItem item, LibraryOptions? libraryOptions)
        {
            var libraryManagerStub = new Mock<ILibraryManager>();
            libraryManagerStub
                .Setup(x => x.GetLibraryOptions(item))
                .Returns(() => libraryOptions!);

            return new MetaSharkOrdinaryItemLibraryCapabilityResolver(libraryManagerStub.Object);
        }

        private static LibraryOptions CreateLibraryOptions(params TypeOptions[] typeOptions)
        {
            return new LibraryOptions
            {
                TypeOptions = typeOptions,
            };
        }

        private static TypeOptions CreateTypeOptions(
            string type,
            string[]? metadataFetchers = null,
            string[]? imageFetchers = null)
        {
            return new TypeOptions
            {
                Type = type,
                MetadataFetchers = metadataFetchers ?? Array.Empty<string>(),
                ImageFetchers = imageFetchers ?? Array.Empty<string>(),
            };
        }
    }
}
