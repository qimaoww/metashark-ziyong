// <copyright file="LlmSuggestionValidationResult.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    public sealed class LlmSuggestionValidationResult
    {
        private LlmSuggestionValidationResult(bool success, LlmScrapingSuggestion? suggestion, string diagnostic)
        {
            this.Success = success;
            this.Suggestion = suggestion;
            this.Diagnostic = diagnostic;
        }

        public bool Success { get; }

        public LlmScrapingSuggestion? Suggestion { get; }

        public string Diagnostic { get; }

        public static LlmSuggestionValidationResult Succeeded(LlmScrapingSuggestion suggestion)
        {
            return new LlmSuggestionValidationResult(true, suggestion, string.Empty);
        }

        public static LlmSuggestionValidationResult Failed(string diagnostic)
        {
            return new LlmSuggestionValidationResult(false, null, string.IsNullOrWhiteSpace(diagnostic) ? "LLM suggestion invalid." : diagnostic);
        }
    }
}
