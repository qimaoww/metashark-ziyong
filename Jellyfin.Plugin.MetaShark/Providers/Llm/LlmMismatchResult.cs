// <copyright file="LlmMismatchResult.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    public sealed class LlmMismatchResult
    {
        private LlmMismatchResult(bool isMismatch, string reason)
        {
            this.IsMismatch = isMismatch;
            this.Reason = reason;
        }

        public bool IsMismatch { get; }

        public string Reason { get; }

        public static LlmMismatchResult Mismatched(string reason)
        {
            return new LlmMismatchResult(true, reason);
        }

        public static LlmMismatchResult NotMismatched(string reason)
        {
            return new LlmMismatchResult(false, reason);
        }
    }
}
