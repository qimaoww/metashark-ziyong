// <copyright file="LlmApiResult.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Api
{
    public sealed class LlmApiResult
    {
        private LlmApiResult(bool success, string? contentJson, string diagnostic)
        {
            this.Success = success;
            this.ContentJson = contentJson;
            this.Diagnostic = diagnostic;
        }

        public bool Success { get; }

        public string? ContentJson { get; }

        public string Diagnostic { get; }

        public static LlmApiResult Succeeded(string contentJson)
        {
            return new LlmApiResult(true, contentJson, string.Empty);
        }

        public static LlmApiResult Failed(string diagnostic)
        {
            return new LlmApiResult(false, null, string.IsNullOrWhiteSpace(diagnostic) ? "LLM request failed." : diagnostic);
        }
    }
}
