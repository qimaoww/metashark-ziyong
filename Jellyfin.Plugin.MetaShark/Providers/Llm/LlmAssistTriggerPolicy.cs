// <copyright file="LlmAssistTriggerPolicy.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Providers.Llm
{
    using System;
    using System.Diagnostics.CodeAnalysis;
    using Jellyfin.Plugin.MetaShark.Configuration;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.Logging;

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Policy is intentionally injectable for provider composition tests.")]
    [SuppressMessage("StyleCop.CSharp.OrderingRules", "SA1204:Static elements should appear before instance members", Justification = "Policy flow is kept ahead of helpers for readability.")]
    public sealed class LlmAssistTriggerPolicy
    {
        private readonly ILogger<LlmAssistTriggerPolicy>? logger;

        public LlmAssistTriggerPolicy(ILogger<LlmAssistTriggerPolicy>? logger = null)
        {
            this.logger = logger;
        }

        public LlmAssistTriggerDecision Evaluate(LlmAssistTriggerContext context)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (!HasCompleteConfiguration(context.Configuration))
            {
                return this.Rejected(context, "LlmConfigurationMissing");
            }

            if (!IsSupportedMediaType(context.MediaType))
            {
                return this.Rejected(context, "UnsupportedMediaType");
            }

            if (context.IsImageProvider)
            {
                return this.Rejected(context, "ImageProviderRejected");
            }

            if (context.Semantic == DefaultScraperSemantic.AutomaticRefresh)
            {
                return this.Rejected(context, "AutomaticRefreshRejected");
            }

            if (context.AllowOverwriteRefresh && IsOverwriteRefresh(context.HttpContext))
            {
                return this.Allowed(context, "ExplicitOverwriteRefresh");
            }

            if (context.AllowOverwriteRefresh
                && context.HasBridgedExplicitOverwriteMetadataRefreshIntent
                && context.Semantic == DefaultScraperSemantic.UserRefresh)
            {
                return this.Allowed(context, "ExplicitOverwriteRefresh");
            }

            if (context.Semantic == DefaultScraperSemantic.OverwriteRefresh || IsOverwriteRefresh(context.HttpContext))
            {
                return this.Rejected(context, "OverwriteRefreshRejected");
            }

            if (IsExplicitManualMatch(context.HttpContext, context.Semantic))
            {
                return this.Allowed(context, "ManualMatch");
            }

            if (context.Semantic == DefaultScraperSemantic.UserRefresh
                && IsExplicitSearchMissingMetadataRefresh(context.HttpContext))
            {
                return this.Allowed(context, "ExplicitSearchMissingMetadataRefresh");
            }

            if (context.HasBridgedExplicitSearchMissingMetadataRefreshIntent
                && context.Semantic == DefaultScraperSemantic.UserRefresh)
            {
                return this.Allowed(context, "ExplicitSearchMissingMetadataRefresh");
            }

            if (IsExplicitUserRefresh(context.HttpContext, context.Semantic))
            {
                return this.Allowed(context, "ExplicitUserRefresh");
            }

            return this.Rejected(context, "ImplicitRefreshRejected");
        }

        public bool ShouldEvaluateDeterministicMismatch(LlmAssistTriggerContext context)
        {
            return this.Evaluate(context).ShouldTrigger;
        }

        private LlmAssistTriggerDecision Allowed(LlmAssistTriggerContext context, string reason)
        {
            var decision = LlmAssistTriggerDecision.Allowed(reason);
            LlmObservabilityLog.LogLlmAssistTriggerDecision(this.logger, context, decision);
            return decision;
        }

        private LlmAssistTriggerDecision Rejected(LlmAssistTriggerContext context, string reason)
        {
            var decision = LlmAssistTriggerDecision.Rejected(reason);
            LlmObservabilityLog.LogLlmAssistTriggerDecision(this.logger, context, decision);
            return decision;
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

        private static bool IsOverwriteRefresh(HttpContext? httpContext)
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
