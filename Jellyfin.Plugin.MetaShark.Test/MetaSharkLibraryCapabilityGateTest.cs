using System;
using Jellyfin.Plugin.MetaShark.Core;

namespace Jellyfin.Plugin.MetaShark.Test
{
    [TestClass]
    public class MetaSharkLibraryCapabilityGateTest
    {
        [TestMethod]
        public void Evaluate_MetadataCapability_ShouldAllowWhileImageCapabilityIsDeniedForSameResolvedLibrary()
        {
            var evidence = CreateEvidence(metadataAllowed: true, imageAllowed: false);

            var metadataDecision = MetaSharkLibraryCapabilityGate.Evaluate(
                MetaSharkLibraryCapabilityGateInput.ForOrdinaryItem(MetaSharkLibraryCapability.Metadata, evidence));
            var imageDecision = MetaSharkLibraryCapabilityGate.Evaluate(
                MetaSharkLibraryCapabilityGateInput.ForOrdinaryItem(MetaSharkLibraryCapability.Image, evidence));

            Assert.IsTrue(metadataDecision.Allowed);
            Assert.AreEqual(MetaSharkLibraryCapabilityGateReason.Allowed, metadataDecision.Reason);
            Assert.AreEqual(MetaSharkLibraryCapability.Metadata, metadataDecision.Capability);
            Assert.AreEqual(1, metadataDecision.ResolvedLibraries.Count);
            Assert.AreEqual(evidence, metadataDecision.ResolvedLibraries[0]);

            Assert.IsFalse(imageDecision.Allowed);
            Assert.AreEqual(MetaSharkLibraryCapabilityGateReason.CapabilityDisabledForResolvedLibrary, imageDecision.Reason);
            Assert.AreEqual(MetaSharkLibraryCapability.Image, imageDecision.Capability);
            Assert.AreEqual(evidence, imageDecision.ResolvedLibraries[0]);
        }

        [TestMethod]
        public void Evaluate_ImageCapability_ShouldAllowWhileMetadataCapabilityIsDeniedForSameResolvedLibrary()
        {
            var evidence = CreateEvidence(metadataAllowed: false, imageAllowed: true);

            var imageDecision = MetaSharkLibraryCapabilityGate.Evaluate(
                MetaSharkLibraryCapabilityGateInput.ForOrdinaryItem(MetaSharkLibraryCapability.Image, evidence));
            var metadataDecision = MetaSharkLibraryCapabilityGate.Evaluate(
                MetaSharkLibraryCapabilityGateInput.ForOrdinaryItem(MetaSharkLibraryCapability.Metadata, evidence));

            Assert.IsTrue(imageDecision.Allowed);
            Assert.AreEqual(MetaSharkLibraryCapabilityGateReason.Allowed, imageDecision.Reason);
            Assert.AreEqual(MetaSharkLibraryCapability.Image, imageDecision.Capability);
            Assert.AreEqual(evidence, imageDecision.ResolvedLibraries[0]);

            Assert.IsFalse(metadataDecision.Allowed);
            Assert.AreEqual(MetaSharkLibraryCapabilityGateReason.CapabilityDisabledForResolvedLibrary, metadataDecision.Reason);
            Assert.AreEqual(MetaSharkLibraryCapability.Metadata, metadataDecision.Capability);
            Assert.AreEqual(evidence, metadataDecision.ResolvedLibraries[0]);
        }

        [TestMethod]
        public void Evaluate_UnresolvedSharedEntity_ShouldReturnExplicitDenyReasonInsteadOfThrowing()
        {
            var input = MetaSharkLibraryCapabilityGateInput.ForSharedEntityUnresolved(MetaSharkLibraryCapability.Metadata);

            var decision = MetaSharkLibraryCapabilityGate.Evaluate(input);

            Assert.IsFalse(decision.Allowed);
            Assert.AreEqual(MetaSharkLibraryCapabilityGateReason.SharedEntityLibraryUnresolved, decision.Reason);
            Assert.AreEqual(MetaSharkLibraryCapability.Metadata, decision.Capability);
            Assert.AreEqual(0, decision.ResolvedLibraries.Count);
        }

        [TestMethod]
        public void Evaluate_NoResolvedLibraryEvidence_ShouldReturnDenyDecisionInsteadOfThrowing()
        {
            var input = MetaSharkLibraryCapabilityGateInput.ForNoResolvedLibrary(
                MetaSharkLibraryCapability.Image,
                MetaSharkLibraryCapabilityContextKind.OrdinaryItem);

            var decision = MetaSharkLibraryCapabilityGate.Evaluate(input);

            Assert.IsFalse(decision.Allowed);
            Assert.AreEqual(MetaSharkLibraryCapabilityGateReason.NoResolvedLibraryEvidence, decision.Reason);
            Assert.AreEqual(MetaSharkLibraryCapability.Image, decision.Capability);
            Assert.AreEqual(0, decision.ResolvedLibraries.Count);
        }

        [TestMethod]
        public void Evaluate_SharedEntityResolvedLibraries_ShouldAllowWhenAtLeastOneLibraryAllowsCapability()
        {
            var deniedLibrary = new MetaSharkResolvedLibraryCapabilityEvidence(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                "A Library",
                "/library/a",
                "Person",
                false,
                true);
            var allowedLibrary = new MetaSharkResolvedLibraryCapabilityEvidence(
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                "B Library",
                "/library/b",
                "Person",
                true,
                true);

            var decision = MetaSharkLibraryCapabilityGate.Evaluate(
                MetaSharkLibraryCapabilityGateInput.ForSharedEntity(
                    MetaSharkLibraryCapability.Metadata,
                    new[] { allowedLibrary, deniedLibrary }));

            Assert.IsTrue(decision.Allowed);
            Assert.AreEqual(MetaSharkLibraryCapabilityGateReason.Allowed, decision.Reason);
            Assert.AreEqual(2, decision.ResolvedLibraries.Count);
            Assert.AreEqual(deniedLibrary, decision.ResolvedLibraries[0]);
            Assert.AreEqual(allowedLibrary, decision.ResolvedLibraries[1]);
        }

        private static MetaSharkResolvedLibraryCapabilityEvidence CreateEvidence(bool metadataAllowed, bool imageAllowed)
        {
            return new MetaSharkResolvedLibraryCapabilityEvidence(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                "Movies",
                "/library/movies",
                "Movie",
                metadataAllowed,
                imageAllowed);
        }
    }
}
