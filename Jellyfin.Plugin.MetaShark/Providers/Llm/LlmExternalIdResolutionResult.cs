// <copyright file="LlmExternalIdResolutionResult.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Collections.Generic;

    public sealed class LlmExternalIdResolutionResult
    {
        private LlmExternalIdResolutionResult(
            LlmExternalIdResolutionStatus status,
            string diagnostic,
            IReadOnlyList<LlmExternalIdCandidate> verifiedCandidates,
            IReadOnlyList<LlmExternalIdProviderIdWrite> providerIdWrites,
            IReadOnlyList<LlmExternalIdProviderIdWrite> skippedProviderIdWrites)
        {
            this.Status = status;
            this.Diagnostic = diagnostic;
            this.VerifiedCandidates = verifiedCandidates;
            this.ProviderIdWrites = providerIdWrites;
            this.SkippedProviderIdWrites = skippedProviderIdWrites;
        }

        public LlmExternalIdResolutionStatus Status { get; }

        public string Diagnostic { get; }

        public LlmExternalIdCandidate? Candidate => this.VerifiedCandidates.Count == 0 ? null : this.VerifiedCandidates[0];

        public IReadOnlyList<LlmExternalIdCandidate> VerifiedCandidates { get; }

        public IReadOnlyList<LlmExternalIdProviderIdWrite> ProviderIdWrites { get; }

        public IReadOnlyList<LlmExternalIdProviderIdWrite> SkippedProviderIdWrites { get; }

        public bool Success => this.Status == LlmExternalIdResolutionStatus.Succeeded;

        public static LlmExternalIdResolutionResult NotTriggered(string diagnostic)
        {
            return Create(LlmExternalIdResolutionStatus.NotTriggered, NormalizeDiagnostic(diagnostic, "LLM external ID resolution not triggered."));
        }

        public static LlmExternalIdResolutionResult Skipped(string diagnostic)
        {
            return Create(LlmExternalIdResolutionStatus.Skipped, NormalizeDiagnostic(diagnostic, "LLM external ID resolution skipped."));
        }

        public static LlmExternalIdResolutionResult Skipped(
            string diagnostic,
            IReadOnlyList<LlmExternalIdCandidate> verifiedCandidates,
            IReadOnlyList<LlmExternalIdProviderIdWrite> skippedProviderIdWrites)
        {
            return new LlmExternalIdResolutionResult(
                LlmExternalIdResolutionStatus.Skipped,
                NormalizeDiagnostic(diagnostic, "LLM external ID resolution skipped."),
                verifiedCandidates,
                Array.Empty<LlmExternalIdProviderIdWrite>(),
                skippedProviderIdWrites);
        }

        public static LlmExternalIdResolutionResult Rejected(string diagnostic)
        {
            return Create(LlmExternalIdResolutionStatus.Rejected, NormalizeDiagnostic(diagnostic, "LLM external ID candidate rejected."));
        }

        public static LlmExternalIdResolutionResult ValidationFailed(string diagnostic)
        {
            return Create(LlmExternalIdResolutionStatus.ValidationFailed, NormalizeDiagnostic(diagnostic, "LLM external ID validation failed."));
        }

        public static LlmExternalIdResolutionResult VerificationFailed(string diagnostic, LlmExternalIdCandidate? candidate = null)
        {
            var candidates = candidate == null ? Array.Empty<LlmExternalIdCandidate>() : new[] { candidate };
            return new LlmExternalIdResolutionResult(
                LlmExternalIdResolutionStatus.VerificationFailed,
                NormalizeDiagnostic(diagnostic, "LLM external ID verification failed."),
                candidates,
                Array.Empty<LlmExternalIdProviderIdWrite>(),
                Array.Empty<LlmExternalIdProviderIdWrite>());
        }

        public static LlmExternalIdResolutionResult Succeeded(LlmExternalIdCandidate candidate)
        {
            return Succeeded(new[] { candidate }, Array.Empty<LlmExternalIdProviderIdWrite>(), Array.Empty<LlmExternalIdProviderIdWrite>(), "Succeeded");
        }

        public static LlmExternalIdResolutionResult Succeeded(
            IReadOnlyList<LlmExternalIdCandidate> verifiedCandidates,
            IReadOnlyList<LlmExternalIdProviderIdWrite> providerIdWrites,
            IReadOnlyList<LlmExternalIdProviderIdWrite> skippedProviderIdWrites,
            string diagnostic)
        {
            return new LlmExternalIdResolutionResult(
                LlmExternalIdResolutionStatus.Succeeded,
                NormalizeDiagnostic(diagnostic, "Succeeded"),
                verifiedCandidates,
                providerIdWrites,
                skippedProviderIdWrites);
        }

        private static LlmExternalIdResolutionResult Create(LlmExternalIdResolutionStatus status, string diagnostic)
        {
            return new LlmExternalIdResolutionResult(
                status,
                diagnostic,
                Array.Empty<LlmExternalIdCandidate>(),
                Array.Empty<LlmExternalIdProviderIdWrite>(),
                Array.Empty<LlmExternalIdProviderIdWrite>());
        }

        private static string NormalizeDiagnostic(string diagnostic, string fallback)
        {
            return string.IsNullOrWhiteSpace(diagnostic) ? fallback : diagnostic;
        }
    }
}
