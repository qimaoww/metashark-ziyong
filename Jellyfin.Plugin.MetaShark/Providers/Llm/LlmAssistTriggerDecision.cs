// <copyright file="LlmAssistTriggerDecision.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    public sealed class LlmAssistTriggerDecision
    {
        private LlmAssistTriggerDecision(bool shouldTrigger, string reason)
        {
            this.ShouldTrigger = shouldTrigger;
            this.Reason = reason;
        }

        public bool ShouldTrigger { get; }

        public string Reason { get; }

        public static LlmAssistTriggerDecision Allowed(string reason)
        {
            return new LlmAssistTriggerDecision(true, reason);
        }

        public static LlmAssistTriggerDecision Rejected(string reason)
        {
            return new LlmAssistTriggerDecision(false, reason);
        }
    }
}
