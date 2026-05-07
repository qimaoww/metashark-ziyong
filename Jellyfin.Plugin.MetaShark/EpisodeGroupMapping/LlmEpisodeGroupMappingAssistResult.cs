// <copyright file="LlmEpisodeGroupMappingAssistResult.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.EpisodeGroupMapping
{
    public sealed class LlmEpisodeGroupMappingAssistResult
    {
        private LlmEpisodeGroupMappingAssistResult(LlmEpisodeGroupMappingAssistStatus status, string reason, string mappingText, string? selectedGroupId)
        {
            this.Status = status;
            this.Reason = reason;
            this.MappingText = mappingText;
            this.SelectedGroupId = selectedGroupId;
        }

        public LlmEpisodeGroupMappingAssistStatus Status { get; }

        public string Reason { get; }

        public string MappingText { get; }

        public string? SelectedGroupId { get; }

        public bool WroteMapping => this.Status == LlmEpisodeGroupMappingAssistStatus.Updated;

        public static LlmEpisodeGroupMappingAssistResult NotTriggered(string reason, string mappingText)
        {
            return new LlmEpisodeGroupMappingAssistResult(LlmEpisodeGroupMappingAssistStatus.NotTriggered, NormalizeReason(reason), mappingText, null);
        }

        public static LlmEpisodeGroupMappingAssistResult Failed(string reason, string mappingText)
        {
            return new LlmEpisodeGroupMappingAssistResult(LlmEpisodeGroupMappingAssistStatus.Failed, NormalizeReason(reason), mappingText, null);
        }

        public static LlmEpisodeGroupMappingAssistResult Rejected(string reason, string mappingText, string? selectedGroupId = null)
        {
            return new LlmEpisodeGroupMappingAssistResult(LlmEpisodeGroupMappingAssistStatus.Rejected, NormalizeReason(reason), mappingText, selectedGroupId);
        }

        public static LlmEpisodeGroupMappingAssistResult NoChange(string reason, string mappingText, string selectedGroupId)
        {
            return new LlmEpisodeGroupMappingAssistResult(LlmEpisodeGroupMappingAssistStatus.NoChange, NormalizeReason(reason), mappingText, selectedGroupId);
        }

        public static LlmEpisodeGroupMappingAssistResult Updated(string mappingText, string selectedGroupId)
        {
            return new LlmEpisodeGroupMappingAssistResult(LlmEpisodeGroupMappingAssistStatus.Updated, string.Empty, mappingText, selectedGroupId);
        }

        private static string NormalizeReason(string reason)
        {
            return string.IsNullOrWhiteSpace(reason) ? "Unknown" : reason;
        }
    }
}
