// <copyright file="MetaSharkLibraryCapabilityGate.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

#pragma warning disable SA1402
namespace Jellyfin.Plugin.MetaShark.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    public enum MetaSharkLibraryCapability
    {
        Metadata = 0,
        Image = 1,
    }

    public enum MetaSharkLibraryCapabilityContextKind
    {
        OrdinaryItem = 0,
        SharedEntity = 1,
    }

    public enum MetaSharkLibraryCapabilityResolutionState
    {
        Resolved = 0,
        NoResolvedLibraryEvidence = 1,
        SharedEntityLibraryUnresolved = 2,
    }

    public enum MetaSharkLibraryCapabilityGateReason
    {
        Allowed = 0,
        CapabilityDisabledForResolvedLibrary = 1,
        NoResolvedLibraryEvidence = 2,
        SharedEntityLibraryUnresolved = 3,
    }

    public static class MetaSharkLibraryCapabilityGate
    {
        public static MetaSharkLibraryCapabilityDecision Evaluate(MetaSharkLibraryCapabilityGateInput input)
        {
            ArgumentNullException.ThrowIfNull(input);

            if (input.ResolutionState == MetaSharkLibraryCapabilityResolutionState.SharedEntityLibraryUnresolved)
            {
                return new MetaSharkLibraryCapabilityDecision(
                    false,
                    MetaSharkLibraryCapabilityGateReason.SharedEntityLibraryUnresolved,
                    input.Capability,
                    input.ResolvedLibraries);
            }

            if (input.ResolvedLibraries.Count == 0)
            {
                return new MetaSharkLibraryCapabilityDecision(
                    false,
                    MetaSharkLibraryCapabilityGateReason.NoResolvedLibraryEvidence,
                    input.Capability,
                    input.ResolvedLibraries);
            }

            var isAllowed = input.ContextKind == MetaSharkLibraryCapabilityContextKind.SharedEntity
                ? input.ResolvedLibraries.Any(x => x.IsAllowed(input.Capability))
                : input.ResolvedLibraries.All(x => x.IsAllowed(input.Capability));
            return new MetaSharkLibraryCapabilityDecision(
                isAllowed,
                isAllowed ? MetaSharkLibraryCapabilityGateReason.Allowed : MetaSharkLibraryCapabilityGateReason.CapabilityDisabledForResolvedLibrary,
                input.Capability,
                input.ResolvedLibraries);
        }
    }

    public sealed class MetaSharkLibraryCapabilityGateInput
    {
        private MetaSharkLibraryCapabilityGateInput(
            MetaSharkLibraryCapability capability,
            MetaSharkLibraryCapabilityContextKind contextKind,
            MetaSharkLibraryCapabilityResolutionState resolutionState,
            IReadOnlyList<MetaSharkResolvedLibraryCapabilityEvidence> resolvedLibraries)
        {
            this.Capability = capability;
            this.ContextKind = contextKind;
            this.ResolutionState = resolutionState;
            this.ResolvedLibraries = resolvedLibraries;
        }

        public MetaSharkLibraryCapability Capability { get; }

        public MetaSharkLibraryCapabilityContextKind ContextKind { get; }

        public MetaSharkLibraryCapabilityResolutionState ResolutionState { get; }

        public IReadOnlyList<MetaSharkResolvedLibraryCapabilityEvidence> ResolvedLibraries { get; }

        public static MetaSharkLibraryCapabilityGateInput ForOrdinaryItem(
            MetaSharkLibraryCapability capability,
            MetaSharkResolvedLibraryCapabilityEvidence resolvedLibrary)
        {
            ArgumentNullException.ThrowIfNull(resolvedLibrary);

            return new MetaSharkLibraryCapabilityGateInput(
                capability,
                MetaSharkLibraryCapabilityContextKind.OrdinaryItem,
                MetaSharkLibraryCapabilityResolutionState.Resolved,
                new[] { resolvedLibrary });
        }

        public static MetaSharkLibraryCapabilityGateInput ForSharedEntity(
            MetaSharkLibraryCapability capability,
            IEnumerable<MetaSharkResolvedLibraryCapabilityEvidence>? resolvedLibraries)
        {
            return new MetaSharkLibraryCapabilityGateInput(
                capability,
                MetaSharkLibraryCapabilityContextKind.SharedEntity,
                MetaSharkLibraryCapabilityResolutionState.Resolved,
                NormalizeResolvedLibraries(resolvedLibraries));
        }

        public static MetaSharkLibraryCapabilityGateInput ForSharedEntityUnresolved(MetaSharkLibraryCapability capability)
        {
            return new MetaSharkLibraryCapabilityGateInput(
                capability,
                MetaSharkLibraryCapabilityContextKind.SharedEntity,
                MetaSharkLibraryCapabilityResolutionState.SharedEntityLibraryUnresolved,
                Array.Empty<MetaSharkResolvedLibraryCapabilityEvidence>());
        }

        public static MetaSharkLibraryCapabilityGateInput ForNoResolvedLibrary(
            MetaSharkLibraryCapability capability,
            MetaSharkLibraryCapabilityContextKind contextKind)
        {
            return new MetaSharkLibraryCapabilityGateInput(
                capability,
                contextKind,
                MetaSharkLibraryCapabilityResolutionState.NoResolvedLibraryEvidence,
                Array.Empty<MetaSharkResolvedLibraryCapabilityEvidence>());
        }

        private static MetaSharkResolvedLibraryCapabilityEvidence[] NormalizeResolvedLibraries(
            IEnumerable<MetaSharkResolvedLibraryCapabilityEvidence>? resolvedLibraries)
        {
            return resolvedLibraries?
                .OrderBy(x => x.LibraryName, StringComparer.Ordinal)
                .ThenBy(x => x.LibraryPath, StringComparer.Ordinal)
                .ThenBy(x => x.LibraryId)
                .ThenBy(x => x.ItemType, StringComparer.Ordinal)
                .ToArray()
                ?? Array.Empty<MetaSharkResolvedLibraryCapabilityEvidence>();
        }
    }

    public sealed record MetaSharkResolvedLibraryCapabilityEvidence(
        Guid LibraryId,
        string LibraryName,
        string LibraryPath,
        string ItemType,
        bool MetadataAllowed,
        bool ImageAllowed)
    {
        public bool IsAllowed(MetaSharkLibraryCapability capability)
        {
            return capability switch
            {
                MetaSharkLibraryCapability.Metadata => this.MetadataAllowed,
                MetaSharkLibraryCapability.Image => this.ImageAllowed,
                _ => false,
            };
        }
    }

    public sealed record MetaSharkLibraryCapabilityDecision(
        bool Allowed,
        MetaSharkLibraryCapabilityGateReason Reason,
        MetaSharkLibraryCapability Capability,
        IReadOnlyList<MetaSharkResolvedLibraryCapabilityEvidence> ResolvedLibraries);
}
#pragma warning restore SA1402
