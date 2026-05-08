// <copyright file="LlmTmdbIdCorrectionResult.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    public sealed class LlmTmdbIdCorrectionResult
    {
        private LlmTmdbIdCorrectionResult(bool shouldReplace, string? replacementTmdbId, string diagnostic)
        {
            this.ShouldReplace = shouldReplace;
            this.ReplacementTmdbId = replacementTmdbId;
            this.Diagnostic = string.IsNullOrWhiteSpace(diagnostic) ? "LLM TMDb correction returned no replacement." : diagnostic;
        }

        public bool ShouldReplace { get; }

        public string? ReplacementTmdbId { get; }

        public string Diagnostic { get; }

        public static LlmTmdbIdCorrectionResult Verified(string replacementTmdbId, string diagnostic)
        {
            return new LlmTmdbIdCorrectionResult(true, replacementTmdbId, diagnostic);
        }

        public static LlmTmdbIdCorrectionResult NoReplacement(string diagnostic)
        {
            return new LlmTmdbIdCorrectionResult(false, null, diagnostic);
        }
    }
}
