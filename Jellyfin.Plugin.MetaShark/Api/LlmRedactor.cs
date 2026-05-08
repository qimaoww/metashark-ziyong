// <copyright file="LlmRedactor.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Api
{
    using System;
    using System.Text.RegularExpressions;

    internal static class LlmRedactor
    {
        private static readonly Regex ApiKeyPattern = new Regex(@"sk-[A-Za-z0-9_\-]{8,}", RegexOptions.Compiled);

        private static readonly Regex UrlPattern = new Regex("https?://[^\\s\\\"'<>]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex UnixPathPattern = new Regex("(?<![A-Za-z0-9_\\-])/(?:opt|mnt|media|root|home|var|srv|volume\\d*)/[^\\s\\\"'<>]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex WindowsPathPattern = new Regex("[A-Za-z]:\\\\[^\\s\\\"'<>]+|\\\\\\\\[^\\s\\\"'<>]+", RegexOptions.Compiled);

        public static string Redact(string? value, string? apiKey = null, string? prompt = null, string? rawResponse = null)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var redacted = value;
            redacted = ReplaceIfNotEmpty(redacted, apiKey, "[redacted-api-key]");
            redacted = ReplaceIfNotEmpty(redacted, prompt, "[redacted-prompt]");
            redacted = ReplaceIfNotEmpty(redacted, rawResponse, "[redacted-response]");
            redacted = ApiKeyPattern.Replace(redacted, "[redacted-api-key]");
            redacted = UrlPattern.Replace(redacted, "[redacted-url]");
            redacted = UnixPathPattern.Replace(redacted, "[redacted-path]");
            redacted = WindowsPathPattern.Replace(redacted, "[redacted-path]");
            if (!string.Equals(redacted, value, StringComparison.Ordinal))
            {
                return "LLM diagnostic redacted.";
            }

            return redacted;
        }

        private static string ReplaceIfNotEmpty(string value, string? sensitiveValue, string replacement)
        {
            if (string.IsNullOrEmpty(sensitiveValue))
            {
                return value;
            }

            return value.Replace(sensitiveValue, replacement, StringComparison.Ordinal);
        }
    }
}
