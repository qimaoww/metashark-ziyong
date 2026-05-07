// <copyright file="LlmExternalIdCandidateValidationResult.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Collections.Generic;

    public sealed class LlmExternalIdCandidateValidationResult
    {
        private LlmExternalIdCandidateValidationResult(bool success, IReadOnlyList<LlmExternalIdCandidate> candidates, IReadOnlyList<string> diagnostics)
        {
            this.Success = success;
            this.Candidates = candidates;
            this.Diagnostics = diagnostics;
            this.Diagnostic = string.Join(" ", diagnostics);
        }

        public bool Success { get; }

        public LlmExternalIdCandidate? Candidate => this.Candidates.Count == 0 ? null : this.Candidates[0];

        public IReadOnlyList<LlmExternalIdCandidate> Candidates { get; }

        public IReadOnlyList<string> Diagnostics { get; }

        public string Diagnostic { get; }

        public static LlmExternalIdCandidateValidationResult Succeeded(LlmExternalIdCandidate candidate)
        {
            return new LlmExternalIdCandidateValidationResult(true, new[] { candidate }, Array.Empty<string>());
        }

        public static LlmExternalIdCandidateValidationResult Succeeded(IReadOnlyList<LlmExternalIdCandidate> candidates, IReadOnlyList<string> diagnostics)
        {
            return new LlmExternalIdCandidateValidationResult(true, candidates, diagnostics);
        }

        public static LlmExternalIdCandidateValidationResult Failed(string diagnostic)
        {
            var normalized = string.IsNullOrWhiteSpace(diagnostic) ? "LLM external ID candidate invalid." : diagnostic;
            return new LlmExternalIdCandidateValidationResult(false, Array.Empty<LlmExternalIdCandidate>(), new[] { normalized });
        }
    }
}
