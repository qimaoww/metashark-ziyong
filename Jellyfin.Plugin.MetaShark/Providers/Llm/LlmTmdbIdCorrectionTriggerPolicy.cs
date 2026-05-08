// <copyright file="LlmTmdbIdCorrectionTriggerPolicy.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Diagnostics.CodeAnalysis;
    using Jellyfin.Plugin.MetaShark.Configuration;
    using Microsoft.AspNetCore.Http;

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Policy is intentionally injectable for provider composition tests.")]
    public sealed class LlmTmdbIdCorrectionTriggerPolicy
    {
        public const string ConfigurationMissingReason = "LlmTmdbIdCorrectionConfigurationMissing";

        public const string UnsupportedMediaTypeReason = "UnsupportedMediaType";

        public const string ImageProviderRejectedReason = "ImageProviderRejected";

        public const string AutomaticRefreshRejectedReason = "AutomaticRefreshRejected";

        public const string ManualMatchReason = "ManualMatch";

        public const string ExplicitOverwriteRefreshReason = "ExplicitOverwriteRefresh";

        public const string ExplicitSearchMissingMetadataRefreshReason = "ExplicitSearchMissingMetadataRefresh";

        public const string ExplicitUserRefreshReason = "ExplicitUserRefresh";

        public const string ImplicitRefreshRejectedReason = "ImplicitRefreshRejected";

        public LlmAssistTriggerDecision Evaluate(LlmAssistTriggerContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (!HasCompleteCorrectionConfiguration(context.Configuration))
            {
                return LlmAssistTriggerDecision.Rejected(ConfigurationMissingReason);
            }

            if (!IsSupportedMediaType(context.MediaType))
            {
                return LlmAssistTriggerDecision.Rejected(UnsupportedMediaTypeReason);
            }

            if (context.IsImageProvider)
            {
                return LlmAssistTriggerDecision.Rejected(ImageProviderRejectedReason);
            }

            if (context.Semantic == DefaultScraperSemantic.AutomaticRefresh)
            {
                return LlmAssistTriggerDecision.Rejected(AutomaticRefreshRejectedReason);
            }

            if (IsExplicitManualMatch(context.HttpContext, context.Semantic))
            {
                return LlmAssistTriggerDecision.Allowed(ManualMatchReason);
            }

            if (IsExplicitOverwriteRefresh(context.HttpContext)
                && context.Semantic is DefaultScraperSemantic.OverwriteRefresh or DefaultScraperSemantic.UserRefresh)
            {
                return LlmAssistTriggerDecision.Allowed(ExplicitOverwriteRefreshReason);
            }

            if (IsExplicitSearchMissingMetadataRefresh(context.HttpContext)
                && context.Semantic == DefaultScraperSemantic.UserRefresh)
            {
                return LlmAssistTriggerDecision.Allowed(ExplicitSearchMissingMetadataRefreshReason);
            }

            if (IsExplicitUserRefresh(context.HttpContext, context.Semantic))
            {
                return LlmAssistTriggerDecision.Allowed(ExplicitUserRefreshReason);
            }

            return LlmAssistTriggerDecision.Rejected(ImplicitRefreshRejectedReason);
        }

        private static bool HasCompleteCorrectionConfiguration(PluginConfiguration? configuration)
        {
            return configuration != null
                && configuration.EnableLlmTmdbIdCorrection
                && configuration.EnableLlmAssist
                && !string.IsNullOrWhiteSpace(configuration.LlmBaseUrl)
                && !string.IsNullOrWhiteSpace(configuration.LlmModel)
                && !string.IsNullOrWhiteSpace(configuration.LlmApiKey);
        }

        private static bool IsSupportedMediaType(string? mediaType)
        {
            return string.Equals(mediaType, "Movie", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mediaType, "Series", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsExplicitManualMatch(HttpContext? httpContext, DefaultScraperSemantic semantic)
        {
            var request = httpContext?.Request;
            return semantic == DefaultScraperSemantic.ManualMatch
                && request != null
                && HttpMethods.IsPost(request.Method)
                && IsManualMatchRoute(request.Path.Value);
        }

        private static bool IsExplicitUserRefresh(HttpContext? httpContext, DefaultScraperSemantic semantic)
        {
            var request = httpContext?.Request;
            return semantic == DefaultScraperSemantic.UserRefresh
                && request != null
                && HttpMethods.IsPost(request.Method)
                && IsRefreshRoute(request.Path.Value)
                && !IsExplicitSearchMissingMetadataRefresh(httpContext)
                && !IsExplicitOverwriteRefresh(httpContext)
                && !HasQueryValue(request, "metadataRefreshMode", "FullRefresh")
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
                && HasQueryValue(request, "false", "replaceAllMetadata", "ReplaceAllMetadata");
        }

        private static bool IsExplicitOverwriteRefresh(HttpContext? httpContext)
        {
            var request = httpContext?.Request;
            return request != null
                && HttpMethods.IsPost(request.Method)
                && IsRefreshRoute(request.Path.Value)
                && HasTrueQueryValue(request, "replaceAllMetadata", "ReplaceAllMetadata");
        }

        private static bool IsManualMatchRoute(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length >= 3
                && string.Equals(segments[0], "Items", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[1], "RemoteSearch", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[2], "Apply", StringComparison.OrdinalIgnoreCase);
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

        private static bool HasQueryValue(HttpRequest request, string expectedValue, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (HasQueryValue(request, key, expectedValue))
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
