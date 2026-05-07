// <copyright file="LlmAssistTriggerPolicy.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Diagnostics.CodeAnalysis;
    using Jellyfin.Plugin.MetaShark.Configuration;
    using Microsoft.AspNetCore.Http;

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Policy is intentionally injectable for provider composition tests.")]
    public sealed class LlmAssistTriggerPolicy
    {
        public LlmAssistTriggerDecision Evaluate(LlmAssistTriggerContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (!HasCompleteConfiguration(context.Configuration))
            {
                return LlmAssistTriggerDecision.Rejected("LlmConfigurationMissing");
            }

            if (!IsSupportedMediaType(context.MediaType))
            {
                return LlmAssistTriggerDecision.Rejected("UnsupportedMediaType");
            }

            if (context.IsImageProvider)
            {
                return LlmAssistTriggerDecision.Rejected("ImageProviderRejected");
            }

            if (context.Semantic == DefaultScraperSemantic.AutomaticRefresh)
            {
                return LlmAssistTriggerDecision.Rejected("AutomaticRefreshRejected");
            }

            if (context.Semantic == DefaultScraperSemantic.OverwriteRefresh || IsOverwriteRefresh(context.HttpContext))
            {
                return LlmAssistTriggerDecision.Rejected("OverwriteRefreshRejected");
            }

            if (context.Semantic == DefaultScraperSemantic.ManualMatch)
            {
                return LlmAssistTriggerDecision.Allowed("ManualMatch");
            }

            if (IsExplicitSearchMissingMetadataRefresh(context.HttpContext))
            {
                return LlmAssistTriggerDecision.Allowed("ExplicitSearchMissingMetadataRefresh");
            }

            if (IsExplicitUserRefresh(context.HttpContext, context.Semantic))
            {
                return LlmAssistTriggerDecision.Allowed("ExplicitUserRefresh");
            }

            if (context.Semantic == DefaultScraperSemantic.UserRefresh && context.HttpContext == null)
            {
                return LlmAssistTriggerDecision.Allowed("UserRefreshWithoutHttpContext");
            }

            return LlmAssistTriggerDecision.Rejected("ImplicitRefreshRejected");
        }

        public bool ShouldEvaluateDeterministicMismatch(LlmAssistTriggerContext context)
        {
            return this.Evaluate(context).ShouldTrigger;
        }

        private static bool HasCompleteConfiguration(PluginConfiguration? configuration)
        {
            return configuration != null
                && configuration.EnableLlmAssist
                && !string.IsNullOrWhiteSpace(configuration.LlmBaseUrl)
                && !string.IsNullOrWhiteSpace(configuration.LlmModel)
                && !string.IsNullOrWhiteSpace(configuration.LlmApiKey);
        }

        private static bool IsSupportedMediaType(string? mediaType)
        {
            return string.Equals(mediaType, "Movie", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mediaType, "Series", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mediaType, "Season", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mediaType, "Episode", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsExplicitUserRefresh(HttpContext? httpContext, DefaultScraperSemantic semantic)
        {
            var request = httpContext?.Request;
            return semantic == DefaultScraperSemantic.UserRefresh
                && request != null
                && HttpMethods.IsPost(request.Method)
                && IsRefreshRoute(request.Path.Value)
                && !IsExplicitSearchMissingMetadataRefresh(httpContext)
                && HasAnyQueryKey(request, "metadataRefreshMode", "replaceAllMetadata", "ReplaceAllMetadata");
        }

        private static bool IsExplicitSearchMissingMetadataRefresh(HttpContext? httpContext)
        {
            var request = httpContext?.Request;
            if (request == null || !HttpMethods.IsPost(request.Method) || !IsRefreshRoute(request.Path.Value))
            {
                return false;
            }

            return HasQueryValue(request, "metadataRefreshMode", "FullRefresh")
                && HasQueryValue(request, "replaceAllMetadata", "false");
        }

        private static bool IsOverwriteRefresh(HttpContext? httpContext)
        {
            var request = httpContext?.Request;
            return request != null
                && HttpMethods.IsPost(request.Method)
                && IsRefreshRoute(request.Path.Value)
                && HasTrueQueryValue(request, "replaceAllMetadata", "ReplaceAllMetadata");
        }

        private static bool IsRefreshRoute(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length == 3
                && string.Equals(segments[0], "Items", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParse(segments[1], out _)
                && string.Equals(segments[2], "Refresh", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasAnyQueryKey(HttpRequest request, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (request.Query.ContainsKey(key))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasQueryValue(HttpRequest request, string key, string expectedValue)
        {
            if (!request.Query.TryGetValue(key, out var values))
            {
                return false;
            }

            foreach (var value in values)
            {
                if (string.Equals(value?.Trim(), expectedValue, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasTrueQueryValue(HttpRequest request, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (!request.Query.TryGetValue(key, out var values))
                {
                    continue;
                }

                foreach (var value in values)
                {
                    if (bool.TryParse(value, out var parsed) && parsed)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
