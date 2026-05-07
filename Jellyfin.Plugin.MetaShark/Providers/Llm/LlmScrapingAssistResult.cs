// <copyright file="LlmScrapingAssistResult.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    public sealed class LlmScrapingAssistResult
    {
        private LlmScrapingAssistResult(LlmScrapingAssistStatus status, string diagnostic)
        {
            this.Status = status;
            this.Diagnostic = diagnostic;
        }

        public LlmScrapingAssistStatus Status { get; }

        public string Diagnostic { get; }

        public LlmPromptContext? SanitizedContext { get; private set; }

        public LlmScrapingSuggestion? Suggestion { get; private set; }

        public LlmSearchHints SearchHints { get; private set; } = new LlmSearchHints();

        public bool Triggered => this.Status != LlmScrapingAssistStatus.NotTriggered;

        public static LlmScrapingAssistResult NotTriggered(string diagnostic)
        {
            return new LlmScrapingAssistResult(LlmScrapingAssistStatus.NotTriggered, diagnostic);
        }

        public static LlmScrapingAssistResult Failed(string diagnostic, LlmPromptContext? sanitizedContext = null)
        {
            return new LlmScrapingAssistResult(LlmScrapingAssistStatus.Failed, diagnostic)
            {
                SanitizedContext = sanitizedContext,
            };
        }

        public static LlmScrapingAssistResult Succeeded(LlmPromptContext sanitizedContext, LlmScrapingSuggestion suggestion, LlmSearchHints searchHints)
        {
            return new LlmScrapingAssistResult(LlmScrapingAssistStatus.Succeeded, "Succeeded")
            {
                SanitizedContext = sanitizedContext,
                Suggestion = suggestion,
                SearchHints = searchHints,
            };
        }
    }
}
