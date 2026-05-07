// <copyright file="LlmMetadataMergePolicy.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Diagnostics.CodeAnalysis;
    using Jellyfin.Plugin.MetaShark.Configuration;
    using MediaBrowser.Controller.Providers;

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Policy is intentionally injectable for provider composition tests.")]
    public sealed class LlmMetadataMergePolicy
    {
        public LlmMetadataMergeResult Apply<TItem>(MetadataResult<TItem> metadataResult, LlmScrapingSuggestion? suggestion, PluginConfiguration? configuration)
            where TItem : MediaBrowser.Controller.Entities.BaseItem, new()
        {
            ArgumentNullException.ThrowIfNull(metadataResult);
            if (suggestion == null)
            {
                return LlmMetadataMergeResult.Skipped("SuggestionMissing");
            }

            if (suggestion.Confidence < (configuration?.LlmConfidenceThreshold ?? new PluginConfiguration().LlmConfidenceThreshold))
            {
                return LlmMetadataMergeResult.Skipped("ConfidenceBelowThreshold");
            }

            metadataResult.Item ??= new TItem();
            var item = metadataResult.Item;
            var changedFields = new System.Collections.Generic.List<string>();

            if (configuration?.LlmAllowTextCompletion == true)
            {
                ApplyAllowedTextCompletion(item, suggestion, changedFields);
            }

            return changedFields.Count == 0
                ? LlmMetadataMergeResult.Skipped("NoAllowedEmptyField")
                : LlmMetadataMergeResult.AppliedResult(changedFields.ToArray());
        }

        public LlmSearchHints CreateSearchHints(LlmScrapingSuggestion? suggestion, PluginConfiguration? configuration)
        {
            if (suggestion == null || suggestion.Confidence < (configuration?.LlmConfidenceThreshold ?? new PluginConfiguration().LlmConfidenceThreshold))
            {
                return new LlmSearchHints();
            }

            return new LlmSearchHints
            {
                Title = NormalizeText(suggestion.Title),
                Year = suggestion.Year,
            };
        }

        private static void ApplyAllowedTextCompletion(MediaBrowser.Controller.Entities.BaseItem item, LlmScrapingSuggestion suggestion, System.Collections.Generic.List<string> changedFields)
        {
            ApplyIfEmpty(() => item.Name, value => item.Name = value, suggestion.Title, nameof(item.Name), changedFields);
            ApplyIfEmpty(() => item.OriginalTitle, value => item.OriginalTitle = value, suggestion.OriginalTitle, nameof(item.OriginalTitle), changedFields);
            ApplyIfEmpty(() => item.Overview, value => item.Overview = value, suggestion.Overview, nameof(item.Overview), changedFields);
        }

        private static void ApplyIfEmpty(Func<string?> getCurrentValue, Action<string> setValue, string? candidateValue, string fieldName, System.Collections.Generic.List<string> changedFields)
        {
            var normalizedCandidate = NormalizeText(candidateValue);
            if (string.IsNullOrWhiteSpace(normalizedCandidate) || !string.IsNullOrWhiteSpace(getCurrentValue()))
            {
                return;
            }

            setValue(normalizedCandidate);
            changedFields.Add(fieldName);
        }

        private static string? NormalizeText(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
